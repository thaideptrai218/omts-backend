using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>The input items and rafts named on the lines of a receipt.</summary>
public sealed record ReceiptLineRefs(IReadOnlyDictionary<Guid, FarmingInputItem> Items, IReadOnlyDictionary<Guid, Raft> Rafts);

/// <summary>
/// PURCHASE-01 checks (BR-PURCHASE-01/03). A Draft needs only a supplier In Use; what it holds must still be valid (no
/// Discontinued item, zone or raft, no raft the caller may not choose, no time received later than now, a receiver who is an
/// active Owner or staff account). Completing needs every line complete; the zone and the rafts stay optional. MSG-GEN-01
/// names the first invalid field, <c>fields</c> all of them.
/// </summary>
public sealed class PurchaseReceiptValidator(
    ISupplierRepository suppliers,
    IRaftRepository rafts,
    IFarmingInputItemRepository items,
    IFarmingZoneRepository zones,
    IAccountRepository accounts,
    PurchaseReceiptAccess access,
    TimeProvider timeProvider)
{
    // Upper bounds well inside decimal(18,0) / decimal(18,3), also for the sum of many lines.
    public const decimal MaxAmount = 100_000_000_000_000m;
    public const decimal MaxQuantity = 1_000_000_000m;
    public const int MaxLines = 200;
    public const int MaxNotesLength = 1000;
    public const int MaxSupplierLotCodeLength = 100;

    /// <summary>How far ahead of the server clock a time received may be: a client clock a little fast is not "in the future".</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>A quantity has at most 2 decimals (PM 03/11); the column keeps its 3 for rows saved before the rule.</summary>
    public static bool HasAllowedDecimals(decimal quantity) => decimal.Round(quantity, 2) == quantity;

    /// <summary>Whole VND from 0 to <see cref="MaxAmount"/>.</summary>
    public static bool IsWholeAmount(decimal amount) => amount >= 0 && amount <= MaxAmount && amount == decimal.Truncate(amount);

    /// <summary>A line discount is whole VND off each unit, never more than the unit price (when the line has one).</summary>
    public static bool DiscountAllowed(decimal discount, decimal? unitPrice) => IsWholeAmount(discount) && (unitPrice is not { } price || discount <= price);

    /// <summary>True when the time received is not later than now (with <see cref="ClockTolerance"/>).</summary>
    public bool NotInFuture(DateTimeOffset receivedAt) => receivedAt <= timeProvider.GetUtcNow() + ClockTolerance;

    /// <summary>Whether the account may be named as the receiver: an Active Owner or staff account.</summary>
    public async Task<bool> IsReceiverAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await accounts.ListActiveInternalAsync(cancellationToken)).Any(a => a.Id == accountId);

    /// <summary>Whether the zone exists and is In Use (GBR-01).</summary>
    public async Task<bool> ZoneInUseAsync(Guid zoneId, CancellationToken cancellationToken) =>
        (await zones.GetByIdsAsync([zoneId], cancellationToken)).Any(z => z.StatusId == StatusIds.Usage.InUse);

    /// <summary>Checks of every save, Draft or not; returns the items and rafts of the lines.</summary>
    public async Task<(ReceiptLineRefs Refs, AppError? Error)> CheckDraftAsync(SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        // A Discontinued supplier or item cannot be chosen, not even on a Draft (GBR-01).
        if (request.SupplierId is not { } supplierId || await suppliers.GetForEditAsync(supplierId, cancellationToken) is not { StatusId: StatusIds.Usage.InUse })
        {
            invalid.Add("Supplier");
        }

        if (request.ReceiptDate is { } date && date > timeProvider.Today())
        {
            invalid.Add("Receipt date");
        }

        if (request.ReceivedAt is { } receivedAt && !NotInFuture(receivedAt))
        {
            invalid.Add("Received at");
        }

        if (request.ReceivedById is { } receiverId && !await IsReceiverAsync(receiverId, cancellationToken))
        {
            invalid.Add("Received by");
        }

        if (request.Notes?.Trim().Length > MaxNotesLength)
        {
            invalid.Add("Notes");
        }

        if (!IsWholeAmount(request.Discount))
        {
            invalid.Add("Discount");
        }

        if (request.ZoneId is { } zoneId && !await ZoneInUseAsync(zoneId, cancellationToken))
        {
            invalid.Add("Zone");
        }

        var lines = request.Lines ?? [];
        if (lines.Count > MaxLines)
        {
            invalid.Add("Lines");
        }

        var refs = await LoadAsync(lines.Select(l => l.ItemId), lines.Select(l => l.RaftId), cancellationToken);
        var raftZones = await access.RaftZonesAsync(cancellationToken);
        for (var i = 0; i < lines.Count; i++)
        {
            FarmingInputItem? item = null;
            if (lines[i].ItemId is { } itemId && (!refs.Items.TryGetValue(itemId, out item) || item.StatusId != StatusIds.Usage.InUse))
            {
                invalid.Add($"Line {i + 1} item");
            }

            if (lines[i].SupplierLotCode?.Trim().Length > MaxSupplierLotCodeLength)
            {
                invalid.Add($"Line {i + 1} supplier lot code");
            }

            // A Draft line may have no item yet (BR-PURCHASE-03): its raft is then checked without the group, which needs the item.
            if (lines[i].RaftId is { } raftId && !RaftAllowed(item, refs.Rafts.GetValueOrDefault(raftId), raftZones, itemRequired: lines[i].ItemId is not null))
            {
                invalid.Add($"Line {i + 1} raft");
            }
        }

        if (invalid.Count > 0)
        {
            return (refs, AppError.InvalidFields(invalid));
        }

        // At most 2 decimals, whole prices stored as decimal(18,0): refuse what the rule forbids or the database would round or overflow.
        var badLine = lines.ToList().FindIndex(l => !LineValuesAllowed(l.Quantity, l.UnitPrice, l.Discount ?? 0));
        return (refs, badLine >= 0 ? InvalidLine(badLine + 1) : null);
    }

    /// <summary>
    /// The values a line may hold, each when given: a quantity above 0 with at most 2 decimals, a whole unit price of 0 or more,
    /// a whole discount per unit of at most the unit price, and an amount inside the bounds.
    /// </summary>
    public static bool LineValuesAllowed(decimal? quantity, decimal? unitPrice, decimal discount) =>
        (quantity is not { } q || (q > 0 && q <= MaxQuantity && HasAllowedDecimals(q)))
        && (unitPrice is not { } p || IsWholeAmount(p))
        && DiscountAllowed(discount, unitPrice)
        && (quantity is not { } lq || unitPrice is not { } lp || lq * lp <= MaxAmount);

    /// <summary>Full check before Draft → Completed, on the receipt as it will be saved (also by a later complete).</summary>
    public async Task<AppError?> CheckCompleteAsync(PurchaseReceipt receipt, ReceiptLineRefs refs, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        if (await suppliers.GetForEditAsync(receipt.SupplierId, cancellationToken) is not { StatusId: StatusIds.Usage.InUse })
        {
            invalid.Add("Supplier");
        }

        if (receipt.ReceiptDate > timeProvider.Today())
        {
            invalid.Add("Receipt date");
        }

        var lines = receipt.Lines.OrderBy(l => l.LineNumber).ToList();
        if (lines.Count == 0)
        {
            invalid.Add("Lines");
        }

        // The rafts are checked again: a pair may have been revoked or a raft discontinued since the Draft was saved.
        var raftZones = await access.RaftZonesAsync(cancellationToken);
        foreach (var line in lines)
        {
            FarmingInputItem? item = null;
            if (line.ItemId is not { } itemId || !refs.Items.TryGetValue(itemId, out item) || item.StatusId != StatusIds.Usage.InUse)
            {
                invalid.Add($"Line {line.LineNumber} item");
            }

            if (line.RaftId is { } raftId && !RaftAllowed(item, refs.Rafts.GetValueOrDefault(raftId), raftZones))
            {
                invalid.Add($"Line {line.LineNumber} raft");
            }
        }

        // The zone is optional; one given must still be In Use (it may have been discontinued since the Draft was saved).
        if (receipt.ZoneId is { } zoneId && !await ZoneInUseAsync(zoneId, cancellationToken))
        {
            invalid.Add("Zone");
        }

        if (receipt.Discount > PurchaseMoney.Total(receipt.Lines))
        {
            invalid.Add("Discount");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        // BR-PURCHASE-01: a quantity greater than 0 and a unit price of 0 or more; a Draft saved before the 2-decimal rule is refused here too.
        var incomplete = lines.FirstOrDefault(l =>
            l.Quantity is not > 0 || !HasAllowedDecimals(l.Quantity.Value) || l.UnitPrice is not >= 0 || !DiscountAllowed(l.Discount, l.UnitPrice));
        return incomplete is null ? null : InvalidLine(incomplete.LineNumber);
    }

    /// <summary>The items and rafts named by lines (unknown ids are simply missing).</summary>
    public async Task<ReceiptLineRefs> LoadAsync(IEnumerable<Guid?> itemIds, IEnumerable<Guid?> raftIds, CancellationToken cancellationToken)
    {
        var foundItems = await items.GetByIdsAsync(itemIds.OfType<Guid>().Distinct().ToList(), cancellationToken);
        var foundRafts = await rafts.GetByIdsAsync(raftIds.OfType<Guid>().Distinct().ToList(), cancellationToken);
        return new ReceiptLineRefs(foundItems.ToDictionary(i => i.Id), foundRafts.ToDictionary(r => r.Id));
    }

    /// <summary>
    /// GBR-04, BR-FARM-05: only a Seed or Material line names a raft, In Use in a zone In Use (GBR-01) that the caller can
    /// access (BR-PURCHASE-01). With <paramref name="itemRequired"/> false (a Draft line without an item yet) the group is not
    /// checked; completing checks it.
    /// </summary>
    public static bool RaftAllowed(FarmingInputItem? item, Raft? raft, PermittedZones raftZones, bool itemRequired = true) =>
        (item is null ? !itemRequired : item.Group is InputItemGroup.Seed or InputItemGroup.Material)
        && raft is { StatusId: StatusIds.Usage.InUse }
        && raft.Zone?.StatusId is null or StatusIds.Usage.InUse
        && (raftZones.AllZones || raftZones.ZoneIds.Contains(raft.ZoneId));

    /// <summary>MSG-PURCHASE-10: a line quantity changed to less than what was seeded from it and returned of it.</summary>
    public static AppError QuantityBelowUsed(PurchaseReceiptLine line, string field, decimal seeded, decimal returned)
    {
        var minimum = seeded + returned;
        return new AppError(
            ErrorKind.Validation,
            UserMessages.QuantityBelowUsed(line.Item?.Name ?? field, minimum, line.Unit, seeded, returned),
            new Dictionary<string, object?>
            {
                ["field"] = field, ["minQuantity"] = minimum, ["seededQuantity"] = seeded, ["returnedQuantity"] = returned,
            });
    }

    /// <summary>MSG-GEN-01 naming the line whose quantity or unit price is invalid (MSG59 before).</summary>
    public static AppError InvalidLine(int lineNumber)
    {
        var field = $"Line {lineNumber}";
        return new AppError(
            ErrorKind.Validation, UserMessages.Invalid(field) with { LegacyCode = MessageCodes.Legacy.Msg59 }, new Dictionary<string, object?> { ["field"] = field });
    }
}
