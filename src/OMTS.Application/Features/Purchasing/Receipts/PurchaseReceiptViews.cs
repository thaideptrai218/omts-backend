using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>Builds the PURCHASE-03.1 receipt detail: lines with rafts, seed lot codes, seeded/returned/returnable quantities, returns.</summary>
public sealed class PurchaseReceiptViews(IPurchaseReceiptRepository receipts, ISeedLotUsageQuery seedUsage, IAccountRepository accounts, IStatusCatalog statusCatalog)
{
    public async Task<PurchaseReceiptView> ViewAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var r = await receipts.GetForEditAsync(receiptId, cancellationToken) ?? throw new InvalidOperationException($"Receipt {receiptId} not found.");
        var seeded = await seedUsage.SeededAsync(r.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var returned = ReturnedQuantities(r);
        var names = await accounts.GetDisplayNamesAsync(r.Returns.Select(rt => rt.RecordedByAccountId).Append(r.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        var figures = PurchaseMoney.Of(r);
        return new PurchaseReceiptView(
            r.Id,
            r.Code,
            new SupplierRef(r.Supplier.Id, r.Supplier.Name, r.Supplier.PhoneNumber),
            r.ReceiptDate,
            figures.Total,
            figures.Returned,
            r.Notes,
            r.StatusId,
            statusCatalog.DisplayNameOf(r.StatusId),
            new AccountRef(r.RecordedByAccountId, names.GetValueOrDefault(r.RecordedByAccountId) ?? string.Empty),
            r.CompletedAt,
            r.CancelledAt,
            r.CancelReason,
            r.Lines.OrderBy(l => l.LineNumber).Select(l =>
            {
                var seededQuantity = seeded.GetValueOrDefault(l.Id);
                var lineReturned = returned.GetValueOrDefault(l.Id);
                return new ReceiptLineView(
                    l.Id, l.LineNumber, l.ItemId, l.Item?.Code, l.Item?.Name, l.Item?.Group, l.Unit, l.Quantity, l.UnitPrice,
                    l.Quantity is null || l.UnitPrice is null ? null : l.LineAmount,
                    l.SupplierLotCode,
                    l.Raft is null ? null : new LineRaftRef(l.Raft.Id, l.Raft.Code, l.Raft.ZoneId, l.Raft.Zone.Code),
                    IsSeedLot(r, l) ? DocumentCodes.SeedLotCode(r.Code, l.LineNumber) : null,
                    seededQuantity, lineReturned, Returnable(l, seededQuantity, lineReturned));
            }).ToList(),
            r.Returns.OrderBy(rt => rt.ReturnDate).ThenBy(rt => rt.Code).Select(rt => new ReceiptReturnView(
                rt.Id, rt.Code, rt.ReturnDate, rt.Reason, rt.Lines.Sum(l => l.ReturnValue), rt.StatusId, statusCatalog.DisplayNameOf(rt.StatusId),
                rt.CancelledAt, rt.CancelReason, new AccountRef(rt.RecordedByAccountId, names.GetValueOrDefault(rt.RecordedByAccountId) ?? string.Empty),
                RowVersionGuard.Encode(rt.RowVersion))).ToList(),
            RowVersionGuard.Encode(r.RowVersion));
    }

    /// <summary>GBR-04: only the Seed lines of a Completed receipt are seed lots.</summary>
    public static bool IsSeedLot(PurchaseReceipt receipt, PurchaseReceiptLine line) =>
        receipt.StatusId == StatusIds.PurchaseReceipt.Completed && line.Item?.Group == InputItemGroup.Seed;

    /// <summary>Per receipt line, the quantity on its returns that are not cancelled: a Cancelled return frees its quantity (BR-PURCHASE-07).</summary>
    public static IReadOnlyDictionary<Guid, decimal> ReturnedQuantities(PurchaseReceipt receipt) =>
        PurchaseMoney.RecordedReturns(receipt).SelectMany(rt => rt.Lines).GroupBy(l => l.ReceiptLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

    /// <summary>BR-PURCHASE-07: received − seeded − already returned. Seeding does not compare with what is left (GBR-04), so the sum can pass the quantity: nothing is left then.</summary>
    public static decimal Returnable(PurchaseReceiptLine line, decimal seeded, decimal returned) =>
        Math.Max(0, (line.Quantity ?? 0) - seeded - returned);
}
