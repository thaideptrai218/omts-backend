using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Application.UnitTests.Features.Sales;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>The links of a lot screen (seed lots, nursery batch, orders of its plan, closing invoice) and the kg of each product of the lot on the ledger.</summary>
public sealed class HarvestLotReferenceReaderTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(7));
    private readonly Guid milky = Guid.NewGuid();
    private readonly Guid fat = Guid.NewGuid();
    private readonly InMemoryProductStock stock = new();

    private HarvestLotReferenceReader Reader(LotOriginRows? origin = null, IReadOnlyList<SalesOrder>? orders = null, params LotInvoiceRow[] invoices) => new(
        StubProxy.Create<IHarvestLotReferenceQuery>(h =>
        {
            h["OriginOfCycleAsync"] = _ => origin ?? new LotOriginRows([], null);
            h["InvoicesAsync"] = args => (IReadOnlyDictionary<Guid, LotInvoiceRow>)invoices.Where(i => ((IReadOnlyCollection<Guid>)args[0]!).Contains(i.Id)).ToDictionary(i => i.Id);
        }),
        StubProxy.Create<IHarvestPlanRepository>(h => h["OrdersByIdsAsync"] = args => (IReadOnlyList<SalesOrder>)(orders ?? []).Where(o => ((IReadOnlyCollection<Guid>)args[0]!).Contains(o.Id)).ToList()),
        stock);

    private ProductStockMovement Row(Guid productId, ProductStockMovementKind kind, decimal kg, Guid? reverses = null, Guid? documentId = null)
    {
        var row = new ProductStockMovement { ProductId = productId, Kind = kind, QuantityKg = kg, ReversesMovementId = reverses, DocumentId = documentId, OccurredAt = At, HarvestLotId = Guid.NewGuid() };
        stock.Add(row);
        return row;
    }

    private static (Guid, string, string) Line(Guid id, string code) => (id, code, $"Product {code}");

    [Fact]
    public async Task Seed_lots_come_with_the_code_of_their_receipt_line_the_nursery_batch_and_the_orders_of_the_plan_by_code()
    {
        var orderB = new SalesOrder { Code = "DH000009" };
        var orderA = new SalesOrder { Code = "DH000004" };
        var receipt = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var origin = new LotOriginRows([new LotSeedLotRow(receipt, "PN000012", 3, "GIO01", "Seed item")], new LotNurseryBatchRow(batch, "NU000002"));
        var lot = new HarvestLot { Cycle = new CultivationCycle { NurseryBatchId = batch } };

        var references = await Reader(origin, [orderB, orderA]).ReferencesAsync(lot, new PlanOfLot(lot.Id, Guid.NewGuid(), "KHT000001", [orderB.Id, orderA.Id]), CancellationToken.None);

        var seed = Assert.Single(references.SeedLots);
        Assert.Equal((receipt, "PN000012", 3, "PN000012-3", "GIO01", "Seed item"), (seed.ReceiptId, seed.ReceiptCode, seed.LineNumber, seed.SeedLotCode, seed.ItemCode, seed.ItemName));
        Assert.Equal((batch, "NU000002"), (references.NurseryBatch!.Id, references.NurseryBatch.Code));
        Assert.Equal(["DH000004", "DH000009"], references.SalesOrders.Select(o => o.Code));
        Assert.Null(references.ClosedByInvoice);
    }

    [Fact]
    public async Task A_lot_outside_a_plan_with_no_seed_record_has_empty_links_and_the_closing_invoice_is_named_when_there_is_one()
    {
        var invoiceId = Guid.NewGuid();
        var open = new HarvestLot { Cycle = new CultivationCycle() };
        var closed = new HarvestLot { Cycle = new CultivationCycle(), ClosedBySalesInvoiceId = invoiceId, StatusId = 2903 };
        var reader = Reader(invoices: new LotInvoiceRow(invoiceId, "HD000031", null, null));

        var none = await reader.ReferencesAsync(open, null, CancellationToken.None);
        var byInvoice = await reader.ReferencesAsync(closed, null, CancellationToken.None);

        Assert.Empty(none.SeedLots);
        Assert.Empty(none.SalesOrders);
        Assert.Null(none.NurseryBatch);
        Assert.Equal((invoiceId, "HD000031"), (byInvoice.ClosedByInvoice!.Id, byInvoice.ClosedByInvoice.Code));
    }

    [Fact]
    public async Task The_sale_rows_of_a_lot_name_the_order_of_their_invoice()
    {
        var withOrder = Guid.NewGuid();
        var withoutOrder = Guid.NewGuid();
        var rows = new[]
        {
            Row(milky, ProductStockMovementKind.Sale, -10m, documentId: withOrder),
            Row(milky, ProductStockMovementKind.Sale, -5m, documentId: withoutOrder),
            Row(milky, ProductStockMovementKind.Harvest, 100m, documentId: Guid.NewGuid()),
        };
        var reader = Reader(invoices: [new LotInvoiceRow(withOrder, "HD1", Guid.NewGuid(), "DH000004"), new LotInvoiceRow(withoutOrder, "HD2", null, null)]);

        var orderOf = await reader.OrderCodesOfSalesAsync(rows, CancellationToken.None);

        Assert.Equal(new Dictionary<Guid, string> { [withOrder] = "DH000004" }, orderOf);
    }

    [Fact]
    public void The_product_balances_count_a_reversal_under_the_kind_it_undoes_and_follow_the_line_order()
    {
        Row(milky, ProductStockMovementKind.Harvest, 100m);
        Row(fat, ProductStockMovementKind.Harvest, 50m);
        var sale = Row(milky, ProductStockMovementKind.Sale, -30m);
        var cancelledSale = Row(milky, ProductStockMovementKind.Sale, -20m);
        Row(milky, ProductStockMovementKind.Reversal, 20m, reverses: cancelledSale.Id);
        var back = Row(milky, ProductStockMovementKind.Return, 30m);
        Row(milky, ProductStockMovementKind.Disposal, -5m);
        Row(fat, ProductStockMovementKind.LotClose, -50m);
        Assert.NotEqual(sale.Id, back.Id);

        var balances = HarvestLotReferenceReader.ProductBalances([Line(milky, "A"), Line(fat, "B")], stock.Rows);

        Assert.Equal(["A", "B"], balances.Select(b => b.ProductCode));
        var a = balances[0];
        Assert.Equal((100m, 30m, 95m, 30m), (a.HarvestedKg, a.SoldKg, a.RemainingKg, a.ReturnedKg));
        var b = balances[1];
        Assert.Equal((50m, 0m, 0m, 0m), (b.HarvestedKg, b.SoldKg, b.RemainingKg, b.ReturnedKg));
    }

    [Fact]
    public void A_product_with_no_row_yet_shows_zeros_and_a_product_only_the_ledger_knows_comes_after_the_lines()
    {
        var stray = Guid.NewGuid();
        var row = Row(stray, ProductStockMovementKind.Harvest, 7m);
        row.Product = new Product { Code = "Z", Name = "Stray" };

        var balances = HarvestLotReferenceReader.ProductBalances([Line(milky, "A")], stock.Rows);

        Assert.Equal(["A", "Z"], balances.Select(b => b.ProductCode));
        Assert.Equal((0m, 0m, 0m), (balances[0].HarvestedKg, balances[0].SoldKg, balances[0].RemainingKg));
        Assert.Equal((7m, 7m, "Stray"), (balances[1].HarvestedKg, balances[1].RemainingKg, balances[1].ProductName));
    }

    [Fact]
    public async Task The_balances_of_a_lot_are_read_from_its_own_ledger_with_the_names_of_its_lines()
    {
        var lot = new HarvestLot { ProductLines = [new HarvestLotProductLine { LineNumber = 1, ProductId = milky, Product = new Product { Code = "A", Name = "Milky" } }] };
        stock.Add(new ProductStockMovement { ProductId = milky, HarvestLotId = lot.Id, Kind = ProductStockMovementKind.Harvest, QuantityKg = 40m, OccurredAt = At });
        stock.Add(new ProductStockMovement { ProductId = milky, HarvestLotId = Guid.NewGuid(), Kind = ProductStockMovementKind.Harvest, QuantityKg = 999m, OccurredAt = At });

        var balances = await Reader().ProductBalancesAsync(lot, new Dictionary<Guid, Product>(), CancellationToken.None);

        var balance = Assert.Single(balances);
        Assert.Equal(("A", "Milky", 40m, 40m), (balance.ProductCode, balance.ProductName, balance.HarvestedKg, balance.RemainingKg));
    }
}
