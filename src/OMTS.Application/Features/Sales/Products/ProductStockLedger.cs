using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>One kg quantity to write on the ledger of a product on a harvest lot: positive adds to the stock. <paramref name="Price"/> is the price of one kg on the document.</summary>
public sealed record ProductPosting(Guid ProductId, Guid LotId, string LotCode, decimal Kg, decimal? Price);

/// <summary>
/// Writes the kg ledger of the products (<see cref="ProductStockMovement"/>). Rows are written with the document in the caller's save and are never changed: a document that is edited or cancelled has its
/// rows undone by reversal rows (same time, opposite kg) and an edit then writes the new rows. A lot recorded before products were split has no lines, so it writes nothing and its sales write nothing either;
/// a product may go below zero, as the cooperative sells what it harvested before it writes the harvest down.
/// </summary>
public sealed class ProductStockLedger(IProductStockRepository stock, ICurrentUser currentUser, TimeProvider timeProvider)
{
    /// <summary>The Harvest rows of a lot: one per product line.</summary>
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

    /// <summary>The Disposal rows of a disposal: its lines leave the stock of their products on their lots.</summary>
    public void PostDisposal(ProductDisposal disposal, IEnumerable<ProductDisposalLine> lines) =>
        Post(
            ProductStockMovementKind.Disposal, disposal.Id, disposal.Code, disposal.DisposedAt, disposal.Reason, null,
            lines.OrderBy(l => l.LineNumber).Select(l => new ProductPosting(l.ProductId, l.HarvestLotId, l.HarvestLot.LotCode, -l.Kg, null)).ToList());

    /// <summary>Undoes the rows of a document that are not undone yet: a reversal row with the opposite kg, naming the row, at the time of that row.</summary>
    public async Task ReverseAsync(Guid documentId, CancellationToken cancellationToken)
    {
        foreach (var row in await stock.UnreversedOfDocumentAsync(documentId, cancellationToken))
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
