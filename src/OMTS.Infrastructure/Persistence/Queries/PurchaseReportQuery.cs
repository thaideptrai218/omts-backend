using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The purchases report on the receipts (Completed, by receipt date) and the returns (Recorded, by return date) of the period. A receipt has no total column, so the caller sums its lines and
/// takes the discount off. The kind keeps seed receipts (returns: seed items) or the others. Read only.
/// </summary>
internal sealed class PurchaseReportQuery(OmtsDbContext db) : IPurchaseReportQuery
{
    public async Task<IReadOnlyList<PurchaseReportDocument>> DocumentsAsync(
        DateOnly from, DateOnly to, IReadOnlyCollection<Guid> supplierIds, PurchaseReceiptKind? kind, CancellationToken cancellationToken)
    {
        var suppliers = supplierIds.ToList();
        var receipts = db.PurchaseReceipts.AsNoTracking()
            .Where(r => r.StatusId == StatusIds.PurchaseReceipt.Completed && r.ReceiptDate >= from && r.ReceiptDate <= to);
        var returns = db.PurchaseReturns.AsNoTracking()
            .Where(r => r.StatusId == StatusIds.PurchaseReturn.Recorded && r.ReturnDate >= from && r.ReturnDate <= to);
        if (suppliers.Count > 0)
        {
            receipts = receipts.Where(r => suppliers.Contains(r.SupplierId));
            returns = returns.Where(r => suppliers.Contains(r.SupplierId));
        }

        if (kind is { } wanted)
        {
            receipts = receipts.Where(r => r.Kind == wanted);
            returns = wanted == PurchaseReceiptKind.Seed
                ? returns.Where(r => r.Lines.Any(l => l.Item.Group == InputItemGroup.Seed))
                : returns.Where(r => r.Lines.Any(l => l.Item.Group != InputItemGroup.Seed));
        }

        var receiptRows = await receipts
            .Select(r => new { r.Id, r.Code, r.ReceiptDate, r.SupplierId, SupplierName = r.Supplier.Name, r.Kind, r.Discount })
            .ToListAsync(cancellationToken);
        var receiptLines = (await receipts.SelectMany(r => r.Lines)
                .Select(l => new
                {
                    l.ReceiptId,
                    ItemId = l.ItemId ?? Guid.Empty,
                    Code = l.Item == null ? string.Empty : l.Item.Code,
                    Name = l.Item == null ? string.Empty : l.Item.Name,
                    Unit = l.Item == null ? string.Empty : l.Item.Unit,
                    Quantity = l.Quantity ?? 0m,
                    l.LineAmount,
                })
                .ToListAsync(cancellationToken))
            .ToLookup(l => l.ReceiptId, l => new PurchaseReportLine(l.ItemId, l.Code, l.Name, l.Unit, l.Quantity, l.LineAmount));

        var returnRows = await returns
            .Select(r => new { r.Id, r.Code, r.ReturnDate, r.SupplierId, SupplierName = r.Supplier.Name, r.Discount })
            .ToListAsync(cancellationToken);
        var returnLinesQuery = returns.SelectMany(r => r.Lines);
        if (kind is { } wantedLines)
        {
            returnLinesQuery = wantedLines == PurchaseReceiptKind.Seed
                ? returnLinesQuery.Where(l => l.Item.Group == InputItemGroup.Seed)
                : returnLinesQuery.Where(l => l.Item.Group != InputItemGroup.Seed);
        }

        var returnLines = (await returnLinesQuery
                .Select(l => new { l.ReturnId, l.ItemId, l.Item.Code, l.Item.Name, l.Item.Unit, l.Quantity, l.ReturnValue })
                .ToListAsync(cancellationToken))
            .ToLookup(l => l.ReturnId, l => new PurchaseReportLine(l.ItemId, l.Code, l.Name, l.Unit, l.Quantity, l.ReturnValue));

        return receiptRows
            .Select(r => new PurchaseReportDocument(r.Id, r.Code, r.ReceiptDate, false, r.SupplierId, r.SupplierName, r.Kind, r.Discount, receiptLines[r.Id].ToList()))
            .Concat(returnRows.Select(r => new PurchaseReportDocument(r.Id, r.Code, r.ReturnDate, true, r.SupplierId, r.SupplierName, null, r.Discount, returnLines[r.Id].ToList())))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> SupplierNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToList();
        return wanted.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Suppliers.AsNoTracking().Where(s => wanted.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
    }
}
