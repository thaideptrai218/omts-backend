using OMTS.Application.Features.Purchasing.Receipts.Dtos;
using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// INP-03 checks. A Draft needs only an existing supplier and zone and known items; completing needs the
/// full set: In Use references, a raft for raft materials, valid lines and amounts.
/// </summary>
public sealed class PurchaseReceiptValidator(
    ISupplierRepository suppliers,
    IFarmingZoneRepository zones,
    IRaftRepository rafts,
    IFarmingInputItemRepository items,
    TimeProvider timeProvider)
{
    // Upper bounds well inside decimal(18,0) / decimal(18,3), also for the sum of many lines.
    private const decimal MaxAmount = 100_000_000_000_000m;
    private const decimal MaxQuantity = 1_000_000_000m;

    /// <summary>Request shape checks shared by drafts and completion; returns the items of the lines.</summary>
    public async Task<(IReadOnlyDictionary<Guid, FarmingInputItem> Items, AppError? Error)> CheckDraftAsync(
        SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        // A Discontinued supplier or item cannot be selected, not even on a Draft (INP-01, INP-02).
        if (request.SupplierId is not { } supplierId || await suppliers.GetForEditAsync(supplierId, cancellationToken) is not { StatusId: StatusIds.Usage.InUse })
        {
            invalid.Add("Supplier");
        }

        if (request.ZoneId is not { } zoneId || (await zones.GetByIdsAsync([zoneId], cancellationToken)).Count == 0)
        {
            invalid.Add("Zone");
        }

        var lines = request.Lines ?? [];
        var itemIds = lines.Select(l => l.ItemId).OfType<Guid>().Distinct().ToList();
        var found = (await items.GetByIdsAsync(itemIds, cancellationToken)).ToDictionary(i => i.Id);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].ItemId is not { } itemId || !found.TryGetValue(itemId, out var lineItem) || lineItem.StatusId != StatusIds.Usage.InUse)
            {
                invalid.Add($"Line {i + 1} item");
            }
        }

        // The raft belongs to the receipt's zone (the caller's grant is checked for that zone), also on a Draft.
        if (request.RaftId is { } raftId && (await rafts.GetForEditAsync(raftId, cancellationToken) is not { } raft || (request.ZoneId is { } raftZone && raft.ZoneId != raftZone)))
        {
            invalid.Add("Raft");
        }

        if (request.DiscountAmount is < 0 or > MaxAmount)
        {
            invalid.Add("Discount amount");
        }

        if (request.AmountPaidNow is < 0 or > MaxAmount)
        {
            invalid.Add("Amount paid now");
        }

        if (request.PaidNowMethod is { } paidNowMethod && !Enum.IsDefined(paidNowMethod))
        {
            invalid.Add("Paid now method");
        }

        CheckText(request.Notes, "Notes", 1000, invalid);
        for (var i = 0; i < lines.Count; i++)
        {
            CheckText(lines[i].SupplierLotCode, $"Line {i + 1} supplier lot code", 100, invalid);
            CheckText(lines[i].ActualSource, $"Line {i + 1} actual source", 500, invalid);
        }

        if (invalid.Count > 0)
        {
            return (found, AppError.InvalidFields(invalid));
        }

        // Stored as decimal(18,3) quantities and decimal(18,0) prices: refuse what the database would round or overflow.
        var badDraftLine = lines.ToList().FindIndex(l =>
            (l.Quantity is { } q && (q <= 0 || q > MaxQuantity || decimal.Round(q, 3) != q))
            || (l.UnitPrice is { } p && (p < 0 || p > MaxAmount || decimal.Truncate(p) != p))
            || (l.Quantity is { } lq && l.UnitPrice is { } lp && lq * lp > MaxAmount));
        if (badDraftLine >= 0)
        {
            return (found, new AppError(ErrorKind.Validation, UserMessages.QuantityOrPriceInvalid, new Dictionary<string, object?> { ["field"] = $"Line {badDraftLine + 1}" }));
        }

        // Amounts are stored as multiples of 1,000 VND (database check), so a Draft is checked too.
        foreach (var (amount, field) in new[] { (request.DiscountAmount ?? 0, "Discount amount"), (request.AmountPaidNow ?? 0, "Amount paid now") })
        {
            if (!PurchaseMoney.IsThousands(amount))
            {
                return (found, new AppError(ErrorKind.Validation, UserMessages.NotThousands, new Dictionary<string, object?> { ["field"] = field }));
            }
        }

        return (found, null);
    }

    /// <summary>Full validation before Draft → Completed, on the receipt as it will be saved.</summary>
    public async Task<AppError?> CheckCompleteAsync(PurchaseReceipt receipt, IReadOnlyDictionary<Guid, FarmingInputItem> lineItems, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        var supplier = await suppliers.GetForEditAsync(receipt.SupplierId, cancellationToken);
        if (supplier?.StatusId != StatusIds.Usage.InUse)
        {
            invalid.Add("Supplier");
        }

        var zone = (await zones.GetByIdsAsync([receipt.ZoneId], cancellationToken)).FirstOrDefault();
        if (zone?.StatusId != StatusIds.Usage.InUse)
        {
            invalid.Add("Zone");
        }

        if (receipt.ReceiptDate == default || receipt.ReceiptDate > timeProvider.Today())
        {
            invalid.Add("Receipt date");
        }

        var lines = receipt.Lines.ToList();
        if (lines.Count == 0)
        {
            invalid.Add("Lines");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (!lineItems.TryGetValue(lines[i].ItemId, out var item) || item.StatusId != StatusIds.Usage.InUse)
            {
                invalid.Add($"Line {i + 1} item");
            }
        }

        var needsRaft = lines.Any(l => lineItems.TryGetValue(l.ItemId, out var item) && item.Group == InputItemGroup.RaftMaterial);
        if (receipt.RaftId is { } raftId)
        {
            var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
            if (raft is null || raft.StatusId != StatusIds.Usage.InUse || raft.ZoneId != receipt.ZoneId)
            {
                invalid.Add("Raft");
            }
        }
        else if (needsRaft)
        {
            invalid.Add("Raft");
        }

        if (receipt.AmountPaidNow > 0 && receipt.PaidNowMethod is null)
        {
            invalid.Add("Paid now method");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var badLine = lines.FindIndex(l => l.Quantity <= 0 || l.UnitPrice < 0 || decimal.Truncate(l.UnitPrice) != l.UnitPrice);
        if (badLine >= 0)
        {
            return new AppError(ErrorKind.Validation, UserMessages.QuantityOrPriceInvalid, new Dictionary<string, object?> { ["field"] = $"Line {badLine + 1}" });
        }

        foreach (var (amount, field) in new[] { (receipt.DiscountAmount, "Discount amount"), (receipt.AmountPaidNow, "Amount paid now") })
        {
            if (!PurchaseMoney.IsThousands(amount))
            {
                return new AppError(ErrorKind.Validation, UserMessages.NotThousands, new Dictionary<string, object?> { ["field"] = field });
            }
        }

        var subtotal = lines.Sum(l => l.LineAmount);
        if (receipt.DiscountAmount > subtotal)
        {
            return ExceedsLimit("Discount amount", subtotal);
        }

        var payable = subtotal - receipt.DiscountAmount;
        return receipt.AmountPaidNow > payable ? ExceedsLimit("Amount paid now", payable) : null;
    }

    public static AppError ExceedsLimit(string field, decimal limit) => new(
        ErrorKind.Validation,
        UserMessages.ExceedsLimit(field, limit.ToString("#,0", CultureInfo.InvariantCulture)),
        new Dictionary<string, object?> { ["field"] = field, ["limit"] = limit });

    private static void CheckText(string? value, string field, int max, List<string> invalid)
    {
        if (value?.Trim().Length > max)
        {
            invalid.Add(field);
        }
    }
}
