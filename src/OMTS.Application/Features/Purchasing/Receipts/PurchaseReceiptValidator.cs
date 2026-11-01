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
/// Discontinued item or raft, no raft the caller may not choose, no date after today). Completing needs every line
/// complete. MSG-GEN-01 names the first invalid field, <c>fields</c> all of them.
/// </summary>
public sealed class PurchaseReceiptValidator(
    ISupplierRepository suppliers,
    IRaftRepository rafts,
    IFarmingInputItemRepository items,
    PurchaseReceiptAccess access,
    TimeProvider timeProvider)
{
    // Upper bounds well inside decimal(18,0) / decimal(18,3), also for the sum of many lines.
    public const decimal MaxAmount = 100_000_000_000_000m;
    public const decimal MaxQuantity = 1_000_000_000m;
    public const int MaxLines = 200;

    /// <summary>Checks of every save, Draft or not; returns the items and rafts of the lines.</summary>
    public async Task<(ReceiptLineRefs Refs, AppError? Error)> CheckDraftAsync(SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        // A Discontinued supplier or item cannot be chosen, not even on a Draft (GBR-01).
        if (request.SupplierId is not { } supplierId || await suppliers.GetForEditAsync(supplierId, cancellationToken) is not { StatusId: StatusIds.Usage.InUse })
        {
            invalid.Add("Supplier");
        }

        if (request.ReceiptDate is { } date && date > timeProvider.Today())
        {
            invalid.Add("Receipt date");
        }

        if (request.Notes?.Trim().Length > 1000)
        {
            invalid.Add("Notes");
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

            if (lines[i].SupplierLotCode?.Trim().Length > 100)
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

        // Stored as decimal(18,3) quantities and decimal(18,0) prices: refuse what the database would round or overflow.
        var badLine = lines.ToList().FindIndex(l =>
            (l.Quantity is { } q && (q <= 0 || q > MaxQuantity || decimal.Round(q, 3) != q))
            || (l.UnitPrice is { } p && (p < 0 || p > MaxAmount || decimal.Truncate(p) != p))
            || (l.Quantity is { } lq && l.UnitPrice is { } lp && lq * lp > MaxAmount));
        return (refs, badLine >= 0 ? InvalidLine(badLine + 1) : null);
    }

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

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        // BR-PURCHASE-01: a quantity greater than 0 and a unit price of 0 or more.
        var incomplete = lines.FirstOrDefault(l => l.Quantity is not > 0 || l.UnitPrice is not >= 0);
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

    /// <summary>MSG-GEN-01 naming the line whose quantity or unit price is invalid (MSG59 before).</summary>
    public static AppError InvalidLine(int lineNumber)
    {
        var field = $"Line {lineNumber}";
        return new AppError(
            ErrorKind.Validation, UserMessages.Invalid(field) with { LegacyCode = MessageCodes.Legacy.Msg59 }, new Dictionary<string, object?> { ["field"] = field });
    }

    public static AppError ExceedsLimit(string field, decimal limit) => new(
        ErrorKind.Validation,
        UserMessages.ExceedsLimit(field, limit.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)),
        new Dictionary<string, object?> { ["field"] = field, ["limit"] = limit });
}
