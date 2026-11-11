using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Products;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The product ledger kept in memory; rows written are visible to the next request, like rows saved.</summary>
public sealed class InMemoryProductStock : IProductStockRepository
{
    public List<ProductStockMovement> Rows { get; } = [];

    public Task<IReadOnlyDictionary<Guid, decimal>> OnHandAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, decimal>>(productIds.ToDictionary(id => id, id => Rows.Where(r => r.ProductId == id).Sum(r => r.QuantityKg)));

    public Task<IReadOnlyList<ProductStockMovement>> UnreversedOfDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProductStockMovement>>(Rows
            .Where(r => r.DocumentId == documentId && r.Kind != ProductStockMovementKind.Reversal && !Rows.Any(x => x.ReversesMovementId == r.Id)).OrderBy(r => r.Sequence).ToList());

    public Task<IReadOnlyDictionary<(Guid ProductId, Guid LotId), decimal>> LotBalancesAsync(
        IReadOnlyCollection<Guid> productIds, IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<(Guid ProductId, Guid LotId), decimal>>(
            Rows.Where(r => r.HarvestLotId is { } lot && lotIds.Contains(lot) && productIds.Contains(r.ProductId))
                .GroupBy(r => (r.ProductId, r.HarvestLotId!.Value)).ToDictionary(g => g.Key, g => g.Sum(r => r.QuantityKg)));

    public Task<IReadOnlyList<ProductMovementRow>> ForProductAsync(Guid productId, bool includeReversals, IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ProductLotRow>> LotsOfProductAsync(Guid productId, IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> HasUnreversedSalesOrDisposalsAsync(Guid lotId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Any(r => r.HarvestLotId == lotId && productIds.Contains(r.ProductId) && r.Kind is ProductStockMovementKind.Sale or ProductStockMovementKind.Disposal
            && !Rows.Any(x => x.ReversesMovementId == r.Id)));

    public void Add(ProductStockMovement movement)
    {
        movement.Sequence = Rows.Count + 1;
        Rows.Add(movement);
    }
}

/// <summary>The writes of the product ledger: the Harvest rows of a lot, the Sale rows of an invoice, the Disposal rows, and the reversal rows that undo a document.</summary>
public sealed class ProductStockLedgerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 9, 0, 0, TimeSpan.FromHours(7));
    private readonly InMemoryProductStock stock = new();
    private readonly Guid milky = Guid.NewGuid();
    private readonly Guid fat = Guid.NewGuid();

    private ProductStockLedger Ledger() => new(stock, new FakeCurrentUser(Guid.NewGuid()), new FixedTimeProvider(At));

    private HarvestLot Lot(bool split) => new()
    {
        LotCode = "KV01-B01-20261112", HarvestedAt = At,
        ProductLines = split
            ? [new HarvestLotProductLine { LineNumber = 1, ProductId = milky, CageCount = 1, Kg = 1 }, new HarvestLotProductLine { LineNumber = 2, ProductId = fat, CageCount = 1, Kg = 1 }]
            : [],
    };

    private SalesInvoice Invoice(HarvestLot lot, params (Guid ProductId, decimal Kg, decimal Price)[] lines)
    {
        var invoice = new SalesInvoice { Code = "HD000007", InvoicedAt = At };
        var number = 0;
        foreach (var (productId, kg, price) in lines)
        {
            invoice.Lines.Add(new SalesInvoiceLine { LineNumber = ++number, ProductId = productId, HarvestLot = lot, HarvestLotId = lot.Id, Kg = kg, UnitPrice = price });
        }

        return invoice;
    }

    [Fact]
    public void A_harvest_writes_one_row_per_product_line_that_adds_to_the_stock()
    {
        var lot = Lot(split: true);

        Ledger().PostHarvest(lot, "B01", [(milky, 125m), (fat, 75m)]);

        Assert.Equal(2, stock.Rows.Count);
        Assert.All(stock.Rows, r => Assert.Equal((ProductStockMovementKind.Harvest, lot.Id, "KV01-B01-20261112", lot.Id, At), (r.Kind, r.DocumentId, r.DocumentCode, r.HarvestLotId, r.OccurredAt)));
        Assert.Equal([125m, 75m], stock.Rows.Select(r => r.QuantityKg));
        Assert.Equal("B01", stock.Rows[0].PartyName);
    }

    [Fact]
    public void A_sale_takes_the_kg_of_each_line_off_its_product_at_the_price_of_the_line_with_the_customer()
    {
        var lot = Lot(split: true);
        var invoice = Invoice(lot, (milky, 40m, 60_000m), (fat, 10m, 75_000m));

        Ledger().PostSale(invoice, new Customer { Code = "KH000009", Name = "Chị Hoa" });

        Assert.Equal([-40m, -10m], stock.Rows.Select(r => r.QuantityKg));
        Assert.Equal([60_000m, 75_000m], stock.Rows.Select(r => r.TransactionPrice));
        Assert.All(stock.Rows, r => Assert.Equal((ProductStockMovementKind.Sale, invoice.Id, "HD000007", "Chị Hoa", "KH000009"), (r.Kind, r.DocumentId, r.DocumentCode, r.PartyName, r.PartyCode)));
    }

    [Fact]
    public void A_sale_from_a_lot_recorded_before_products_writes_nothing_so_it_changes_no_stock()
    {
        var invoice = Invoice(Lot(split: false), (milky, 40m, 60_000m));

        Ledger().PostSale(invoice, null);

        Assert.Empty(stock.Rows);
    }

    [Fact]
    public void A_sale_of_a_product_the_lot_was_not_split_into_writes_no_row()
    {
        var lot = Lot(split: true);

        Ledger().PostSale(Invoice(lot, (Guid.NewGuid(), 40m, 60_000m)), null);

        Assert.Empty(stock.Rows);
    }

    [Fact]
    public async Task Reversing_a_document_writes_the_opposite_rows_naming_the_rows_they_undo_and_only_once()
    {
        var lot = Lot(split: true);
        var ledger = Ledger();
        ledger.PostHarvest(lot, "B01", [(milky, 125m)]);
        var original = stock.Rows.Single();

        await ledger.ReverseAsync(lot.Id, CancellationToken.None);
        await ledger.ReverseAsync(lot.Id, CancellationToken.None);

        Assert.Equal(2, stock.Rows.Count);
        var reversal = stock.Rows[1];
        Assert.Equal((ProductStockMovementKind.Reversal, original.Id, -125m, original.OccurredAt), (reversal.Kind, reversal.ReversesMovementId, reversal.QuantityKg, reversal.OccurredAt));
        Assert.Equal(0m, (await ledger.OnHandAsync([milky], CancellationToken.None))[milky]);
    }

    [Fact]
    public async Task An_edit_is_a_reversal_then_the_new_rows_and_the_stock_follows_the_new_kg()
    {
        var lot = Lot(split: true);
        var ledger = Ledger();
        ledger.PostHarvest(lot, "B01", [(milky, 125m)]);

        await ledger.ReverseAsync(lot.Id, CancellationToken.None);
        ledger.PostHarvest(lot, "B01", [(milky, 100m)]);

        Assert.Equal(100m, (await ledger.OnHandAsync([milky], CancellationToken.None))[milky]);
        Assert.Equal([ProductStockMovementKind.Harvest, ProductStockMovementKind.Reversal, ProductStockMovementKind.Harvest], stock.Rows.Select(r => r.Kind));
    }

    [Fact]
    public void A_disposal_takes_its_kg_off_the_product_on_the_lot_and_the_stock_may_go_below_zero()
    {
        var lot = Lot(split: true);
        var disposal = new ProductDisposal { Code = "XHS000001", DisposedAt = At, Reason = "Hàu chết" };
        disposal.Lines.Add(new ProductDisposalLine { LineNumber = 1, ProductId = milky, HarvestLot = lot, HarvestLotId = lot.Id, Kg = 30m });

        Ledger().PostDisposal(disposal, disposal.Lines);

        var row = stock.Rows.Single();
        Assert.Equal((ProductStockMovementKind.Disposal, "XHS000001", -30m, "Hàu chết", lot.Id), (row.Kind, row.DocumentCode, row.QuantityKg, row.PartyName, row.HarvestLotId));
        Assert.Equal(-30m, stock.Rows.Sum(r => r.QuantityKg));
    }
}
