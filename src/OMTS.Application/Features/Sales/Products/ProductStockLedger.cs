using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>One kg quantity to write on the ledger of a product on a harvest lot: positive adds to the stock. <paramref name="Price"/> is the price of one kg on the document.</summary>
public sealed record ProductPosting(Guid ProductId, Guid LotId, string LotCode, decimal Kg, decimal? Price);

/// <summary>
/// Writes the kg ledger of the products (<see cref="ProductStockMovement"/>). Rows are written with the document in the caller's save and are never changed: a document that is edited or cancelled has its
/// rows undone by reversal rows (same time, opposite kg) and an edit then writes the new rows. A lot recorded before products were split has no lines, so it writes nothing and its sales write nothing either;
/// a product may go below zero, as the cooperative sells what it harvested before it writes the harvest down. A lot writes its Harvest rows when it is weighed (at the weighed kg of each line; weighing
/// again undoes only those rows), and a LotClose row per product when it is closed, which takes what is left of the product on the lot off the stock; reopening the lot undoes only those rows.
/// </summary>
public sealed class ProductStockLedger(IProductStockRepository stock, ICurrentUser currentUser, TimeProvider timeProvider)
{
    /// <summary>The Harvest rows of a lot: one per product line, at the weighed kg of the line.</summary>
    public void PostHarvest(HarvestLot lot, string raftCode, IEnumerable<(Guid ProductId, decimal Kg)> lines) =>
        Post(
            ProductStockMovementKind.Harvest, lot.Id, lot.LotCode, lot.HarvestedAt, raftCode, null,
            lines.Select(l => new ProductPosting(l.ProductId, lot.Id, lot.LotCode, l.Kg, null)).ToList());

    /// <summary>The Sale rows of an invoice: its lines leave the stock of their products on their lots, at the price of the line, when the lot was split into that product.</summary>
    public void PostSale(SalesInvoice invoice, Customer? customer) =>
        Post(
            ProductStockMovementKind.Sale, invoice.Id, invoice.Code, invoice.InvoicedAt, customer?.Name, customer?.Code,
            invoice.Lines.Where(l => l.HarvestLot.ProductLines.Any(p => p.ProductId == l.ProductId))
                .Select(l => new ProductPosting(l.ProductId, l.HarvestLotId, l.HarvestLot.LotCode, -l.Kg, InvoiceMoney.PricePerKg(l.Kg, l.Quantity, l.UnitPrice, l.Amount))).ToList());

    /// <summary>
    /// The Return rows of a sales return: each line taken back for sale puts its kg back on the stock of its product on its lot, at the price of the invoice line, when the lot was split into that
    /// product. A line thrown away writes nothing, as those oysters are lost. <paramref name="lots"/> are the lots of the invoice lines, with their product lines.
    /// </summary>
    public void PostReturn(SalesReturn salesReturn, SalesInvoice invoice, Customer? customer, IReadOnlyDictionary<Guid, HarvestLot> lots)
    {
        var backForSale = salesReturn.Lines.Where(l => l.Outcome == SalesReturnOutcome.BackForSale).Select(l => l.InvoiceLineId).ToHashSet();
        Post(
            ProductStockMovementKind.Return, salesReturn.Id, salesReturn.Code, timeProvider.GetUtcNow(), customer?.Name, customer?.Code,
            invoice.Lines.Where(l => backForSale.Contains(l.Id) && lots.TryGetValue(l.HarvestLotId, out var lot) && lot.ProductLines.Any(p => p.ProductId == l.ProductId))
                .Select(l => new ProductPosting(l.ProductId, l.HarvestLotId, lots[l.HarvestLotId].LotCode, l.Kg, InvoiceMoney.PricePerKg(l.Kg, l.Quantity, l.UnitPrice, l.Amount))).ToList());
    }

    /// <summary>
    /// A cancelled sales return takes its Return rows off the stock again. A lot of the return that is Closed by now had what was left of it taken off at its close, with those kg in it, so its close is
    /// written again for the kg the reversal took off, and a closed lot still holds no stock. <paramref name="salesReturn"/> comes with the lot of each line and the product lines of the lot.
    /// </summary>
    public async Task ReverseReturnAsync(SalesReturn salesReturn, CancellationToken cancellationToken)
    {
        await ReverseAsync(salesReturn.Id, ProductStockMovementKind.Return, cancellationToken);
        var closedLots = salesReturn.Lines.Where(l => l.Outcome == SalesReturnOutcome.BackForSale).Select(l => l.HarvestLot)
            .Where(l => l.StatusId == StatusIds.HarvestLot.Closed).DistinctBy(l => l.Id).ToList();
        foreach (var lot in closedLots)
        {
            await PostLotCloseAsync(lot, cancellationToken);
        }
    }

    /// <summary>The Disposal rows of a disposal: its lines leave the stock of their products on their lots.</summary>
    public void PostDisposal(ProductDisposal disposal, IEnumerable<ProductDisposalLine> lines) =>
        Post(
            ProductStockMovementKind.Disposal, disposal.Id, disposal.Code, disposal.DisposedAt, disposal.Reason, null,
            lines.OrderBy(l => l.LineNumber).Select(l => new ProductPosting(l.ProductId, l.HarvestLotId, l.HarvestLot.LotCode, -l.Kg, null)).ToList());

    /// <summary>
    /// The LotClose rows of a lot being closed: for each product the lot is split into, the kg that is left of it on the lot (the ledger of the lot, with the rows this request has already added) taken off, so a
    /// closed lot holds no stock. A product with nothing left writes no row. A lot with no product lines writes nothing.
    /// </summary>
    public async Task PostLotCloseAsync(HarvestLot lot, CancellationToken cancellationToken)
    {
        var productIds = lot.ProductLines.Select(l => l.ProductId).Distinct().ToList();
        if (productIds.Count == 0)
        {
            return;
        }

        var balances = await stock.LotBalancesAsync(productIds, [lot.Id], cancellationToken);
        Post(
            ProductStockMovementKind.LotClose, lot.Id, lot.LotCode, timeProvider.GetUtcNow(), null, null,
            productIds.Select(id => new ProductPosting(id, lot.Id, lot.LotCode, -balances.GetValueOrDefault((id, lot.Id)), null)).Where(p => p.Kg != 0m).ToList());
    }

    /// <summary>Undoes the rows of a document that are not undone yet: a reversal row with the opposite kg, naming the row, at the time of that row.</summary>
    public Task ReverseAsync(Guid documentId, CancellationToken cancellationToken) => ReverseAsync(documentId, null, cancellationToken);

    /// <summary>The same for the rows of one kind only: weighing a lot again undoes its Harvest rows, and reopening it undoes its LotClose rows, whatever else the lot has on the ledger.</summary>
    public async Task ReverseAsync(Guid documentId, ProductStockMovementKind? kind, CancellationToken cancellationToken)
    {
        foreach (var row in (await stock.UnreversedOfDocumentAsync(documentId, cancellationToken)).Where(r => kind is null || r.Kind == kind))
        {
            Write(new ProductStockMovement
            {
                ProductId = row.ProductId, HarvestLotId = row.HarvestLotId, LotCode = row.LotCode, Kind = ProductStockMovementKind.Reversal, DocumentId = row.DocumentId,
                DocumentCode = row.DocumentCode, OccurredAt = row.OccurredAt, QuantityKg = -row.QuantityKg, TransactionPrice = row.TransactionPrice, PartyName = row.PartyName,
                PartyCode = row.PartyCode, ReversesMovementId = row.Id,
            });
        }
    }

    /// <summary>The kg on hand of each product: the ledger of the database (rows written in this request are not counted).</summary>
    public Task<IReadOnlyDictionary<Guid, decimal>> OnHandAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        stock.OnHandAsync(productIds, cancellationToken);

    private void Post(
        ProductStockMovementKind kind, Guid documentId, string documentCode, DateTimeOffset occurredAt, string? party, string? partyCode, IReadOnlyList<ProductPosting> postings)
    {
        foreach (var posting in postings)
        {
            Write(new ProductStockMovement
            {
                ProductId = posting.ProductId, HarvestLotId = posting.LotId, LotCode = posting.LotCode, Kind = kind, DocumentId = documentId, DocumentCode = documentCode,
                OccurredAt = occurredAt, QuantityKg = posting.Kg, TransactionPrice = posting.Price, PartyName = Truncate(party, 200), PartyCode = Truncate(partyCode, 50),
            });
        }
    }

    private void Write(ProductStockMovement movement)
    {
        movement.RecordedAt = timeProvider.GetUtcNow();
        movement.RecordedByAccountId = currentUser.AccountId;
        stock.Add(movement);
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
