using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>Builds the PURCHASE-03.1 receipt detail: lines with rafts, seed lot codes, seeded/returned/returnable quantities, returns.</summary>
public sealed class PurchaseReceiptViews(
    IPurchaseReceiptRepository receipts, ISeedLotUsageQuery seedUsage, IAccountRepository accounts, IStatusCatalog statusCatalog, SupplierPaymentAllocationService allocations)
{
    public async Task<PurchaseReceiptView> ViewAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var r = await receipts.GetForEditAsync(receiptId, cancellationToken) ?? throw new InvalidOperationException($"Receipt {receiptId} not found.");
        var seeded = await seedUsage.SeededAsync(r.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var returned = ReturnedQuantities(r);
        var accountIds = r.Returns.Select(rt => rt.RecordedByAccountId).Append(r.RecordedByAccountId);
        if (r.ReceivedByAccountId is { } receiverId)
        {
            accountIds = accountIds.Append(receiverId);
        }

        var names = await accounts.GetDisplayNamesAsync(accountIds.Distinct().ToList(), cancellationToken);
        var figures = PurchaseMoney.Of(r);
        var paid = (await allocations.PaidByReceiptAsync([r.SupplierId], cancellationToken)).GetValueOrDefault(r.Id);
        return new PurchaseReceiptView(
            r.Id,
            r.Code,
            new SupplierRef(r.Supplier.Id, r.Supplier.Name, r.Supplier.PhoneNumber, r.Supplier.Code),
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
                    l.Id, l.LineNumber, l.ItemId, l.Item?.Code, l.Item?.Name, l.Item?.Group, l.Unit, l.EnteredQuantity, l.EnteredUnitPrice,
                    l.Quantity is null || l.UnitPrice is null ? null : l.LineAmount,
                    l.SupplierLotCode,
                    l.Raft is null ? null : new LineRaftRef(l.Raft.Id, l.Raft.Code, l.Raft.ZoneId, l.Raft.Zone.Code),
                    IsSeedLot(r, l) ? DocumentCodes.SeedLotCode(r.Code, l.LineNumber) : null,
                    seededQuantity, lineReturned, Returnable(l, seededQuantity, lineReturned), l.EnteredDiscount,
                    l.EnteredUnitPrice is null ? null : PurchaseMoney.ReturnPriceInLineUnit(r, l),
                    l.ItemUnitId, l.UnitFactor, l.Quantity, l.Item?.Unit);
            }).ToList(),
            r.Returns.Where(rt => rt.StatusId != StatusIds.PurchaseReturn.Draft).OrderBy(rt => rt.ReturnedAt).ThenBy(rt => rt.Code).Select(rt => new ReceiptReturnView(
                rt.Id, rt.Code, rt.ReturnDate, rt.Reason, rt.Lines.Sum(l => l.ReturnValue), rt.StatusId, statusCatalog.DisplayNameOf(rt.StatusId),
                rt.CancelledAt, rt.CancelReason, new AccountRef(rt.RecordedByAccountId, names.GetValueOrDefault(rt.RecordedByAccountId) ?? string.Empty),
                RowVersionGuard.Encode(rt.RowVersion), rt.Discount, rt.Lines.Sum(l => l.ReturnValue) - rt.Discount, rt.ReturnedAt)).ToList(),
            RowVersionGuard.Encode(r.RowVersion),
            r.Discount,
            figures.Payable,
            paid,
            r.Zone is { } zone ? new ZoneRef(zone.Id, zone.Code, zone.Name) : null,
            r.ReceivedAt,
            r.ReceivedByAccountId is { } receivedBy ? new AccountRef(receivedBy, names.GetValueOrDefault(receivedBy) ?? string.Empty) : null);
    }

    /// <summary>GBR-04: only the Seed lines of a Completed receipt are seed lots.</summary>
    public static bool IsSeedLot(PurchaseReceipt receipt, PurchaseReceiptLine line) =>
        receipt.StatusId == StatusIds.PurchaseReceipt.Completed && line.Item?.Group == InputItemGroup.Seed;

    /// <summary>Per receipt line, the quantity on its Recorded returns: a Draft holds none and a Cancelled return frees its quantity (BR-PURCHASE-07).</summary>
    public static IReadOnlyDictionary<Guid, decimal> ReturnedQuantities(PurchaseReceipt receipt) =>
        PurchaseMoney.RecordedReturns(receipt).SelectMany(rt => rt.Lines).Where(l => l.ReceiptLineId != null)
            .GroupBy(l => l.ReceiptLineId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

    /// <summary>BR-PURCHASE-07: received − seeded − already returned. Seeding does not compare with what is left (GBR-04), so the sum can pass the quantity: nothing is left then.</summary>
    public static decimal Returnable(PurchaseReceiptLine line, decimal seeded, decimal returned) =>
        Math.Max(0, (line.Quantity ?? 0) - seeded - returned);
}

