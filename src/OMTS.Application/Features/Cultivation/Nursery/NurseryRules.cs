using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>The refusals of the nursery, each with a <c>reasonCode</c> a client can tell apart (MSG-GEN-09 until the SRS has MSG-NURSERY-xx).</summary>
public static class NurseryReasonCodes
{
    public const string NotNursing = "NURSERY_NOT_NURSING";
    public const string NotCompleted = "NURSERY_NOT_COMPLETED";
    public const string RaftHasOpenCycle = "NURSERY_RAFT_HAS_OPEN_CYCLE";
    public const string RaftOtherZone = "NURSERY_RAFT_OTHER_ZONE";
    public const string RaftNotUsable = "NURSERY_RAFT_NOT_USABLE";
    public const string SeedingAboveAllocation = "NURSERY_SEEDING_ABOVE_ALLOCATION";
    public const string SeedingMustUseFullAllocation = "NURSERY_SEEDING_MUST_USE_FULL_ALLOCATION";
    public const string SeedingAlreadyComplete = "NURSERY_SEEDING_ALREADY_COMPLETE";
    public const string SurvivorsBelowSeeded = "NURSERY_SURVIVORS_BELOW_SEEDED";
    public const string AllocationBelowSeeded = "NURSERY_ALLOCATION_BELOW_SEEDED";
    public const string AllocationFrozen = "NURSERY_ALLOCATION_FROZEN";
    public const string CycleStartsBeforeClose = "NURSERY_CYCLE_STARTS_BEFORE_CLOSE";
}

/// <summary>The figures and checks of the nursery as pure functions: the loss, the shares of the survivors and the cap of seeding from a nursery.</summary>
public static class NurseryRules
{
    /// <summary>Pieces put in less survivors; an estimate, as the survivors are counted by eye.</summary>
    public static decimal LossPieces(decimal quantityIn, decimal survivors) => quantityIn - survivors;

    /// <summary>The loss as a percentage of the pieces put in with one decimal; 0 when nothing was put in.</summary>
    public static decimal LossPercent(decimal quantityIn, decimal survivors) =>
        quantityIn > 0 ? decimal.Round((quantityIn - survivors) / quantityIn * 100, 1) : 0m;

    /// <summary>A quantity of pieces as the forms take it: above 0, at most two decimals (as the receipts), within the limit of the receipts.</summary>
    public static bool IsPieces(decimal? quantity) =>
        quantity is > 0 and <= PurchaseReceiptValidator.MaxQuantity && PurchaseReceiptValidator.HasAllowedDecimals(quantity.Value);

    /// <summary>The survivors: from 0 to the pieces put in, at most two decimals.</summary>
    public static bool IsSurvivors(decimal? survivors, decimal quantityIn) =>
        survivors is >= 0 && survivors <= quantityIn && PurchaseReceiptValidator.HasAllowedDecimals(survivors.Value);

    /// <summary>
    /// The shares of the survivors: every raft once with pieces above 0, adding up to the survivors exactly; an empty list only when nothing survived. Null when they are fine, else the hint
    /// of MSG-GEN-01 on the field <c>Allocations</c>.
    /// </summary>
    public static string? AllocationHint(decimal survivors, IReadOnlyList<NurseryAllocationInput> allocations)
    {
        if (allocations.Count == 0)
        {
            return survivors == 0 ? null : "Chọn ít nhất một bè và chia đủ số mảnh sống sót cho các bè.";
        }

        if (allocations.Count > NurseryValues.MaxAllocations || allocations.Any(a => a is null || a.RaftId is null || a.RaftId == Guid.Empty))
        {
            return "Mỗi phần chia cần một bè.";
        }

        if (allocations.Select(a => a.RaftId).Distinct().Count() != allocations.Count)
        {
            return "Mỗi bè chỉ xuất hiện một lần.";
        }

        if (allocations.Any(a => !IsPieces(a.Quantity)))
        {
            return "Số mảnh của mỗi bè phải lớn hơn 0 (tối đa 2 chữ số thập phân).";
        }

        var total = allocations.Sum(a => a.Quantity!.Value);
        return total == survivors ? null : $"Tổng số mảnh chia cho các bè ({total:0.###}) phải bằng đúng số mảnh sống sót ({survivors:0.###}).";
    }

    /// <summary>What can still be hung from a share: the share less what is hung, never below 0.</summary>
    public static decimal Remaining(decimal allocated, decimal seeded) => Math.Max(0m, allocated - seeded);

    /// <summary>
    /// The hard cap of seeding from a nursery (owner decision of 12/11/2026): the seed of the cycle with this session may not pass its share. Null when within it; else 409 with the figures.
    /// The old 110 % and 150 % of the plan do not apply to a cycle from a nursery.
    /// </summary>
    public static AppError? CheckSeeding(decimal allocated, decimal otherSessions, decimal quantity)
    {
        var remaining = Remaining(allocated, otherSessions);
        if (quantity <= remaining)
        {
            return null;
        }

        return AppError.Conflict(
            UserMessages.NurseryRefused($"Chỉ còn {remaining:0.###} trên {allocated:0.###} mảnh đã phân bổ cho chu kỳ này từ đợt ươm; không thể thả {quantity:0.###} mảnh."),
            new Dictionary<string, object?>
            {
                ["reasonCode"] = NurseryReasonCodes.SeedingAboveAllocation, ["field"] = "Quantity", ["allocated"] = allocated, ["seeded"] = otherSessions, ["remaining"] = remaining, ["quantity"] = quantity,
            });
    }

    /// <summary>Two quantities of pieces are the same within the precision of the figures (three decimals).</summary>
    private const decimal SamePiecesWithin = 0.001m;

    /// <summary>
    /// Owner decision of 13/11/2026: a session on a cycle from a nursery hangs 100 % of what is left of the share, so the quantity is not chosen. It is the remaining share (<paramref name="sent"/> may
    /// be left out); a quantity that differs is 400 <c>NURSERY_SEEDING_MUST_USE_FULL_ALLOCATION</c>, and nothing left is 400 <c>NURSERY_SEEDING_ALREADY_COMPLETE</c> (the sessions already hung it all).
    /// A correction passes the other sessions of the cycle, so its quantity keeps the total at the share.
    /// </summary>
    public static (decimal Quantity, AppError? Error) FullSeeding(decimal allocated, decimal otherSessions, decimal? sent)
    {
        var remaining = Remaining(allocated, otherSessions);
        var figures = new Dictionary<string, object?> { ["field"] = "Quantity", ["allocated"] = allocated, ["seeded"] = otherSessions, ["remaining"] = remaining };
        if (remaining <= 0m)
        {
            return (0m, new AppError(
                ErrorKind.Validation, UserMessages.NurseryRefused($"Phần đã chuyển sang bè này ({allocated:0.###} mảnh) đã được thả hết, không còn mảnh nào để thả thêm."),
                new Dictionary<string, object?>(figures) { ["reasonCode"] = NurseryReasonCodes.SeedingAlreadyComplete }));
        }

        if (sent is { } quantity && Math.Abs(quantity - remaining) > SamePiecesWithin)
        {
            return (remaining, new AppError(
                ErrorKind.Validation,
                UserMessages.NurseryRefused($"Thả giống phải dùng đủ phần đã chuyển sang bè: còn {remaining:0.###} mảnh. Nếu số lượng thay đổi, hãy sửa số sống sót hoặc chuyển lại số mảnh giữa các bè trước."),
                new Dictionary<string, object?>(figures) { ["reasonCode"] = NurseryReasonCodes.SeedingMustUseFullAllocation, ["quantity"] = quantity }));
        }

        return (remaining, null);
    }

    /// <summary>The nursery block of a cycle: the batch's pieces put in in proportion to the cycle's share, and what the share lost (an estimate).</summary>
    public static NurserySummary SummaryOf(NurseryCycleFacts facts)
    {
        var share = facts.Survivors > 0 ? facts.Allocated / facts.Survivors : 0m;
        var quantityIn = decimal.Round(facts.QuantityIn * share, 3);
        return new NurserySummary(facts.BatchId, facts.BatchCode, quantityIn, facts.Allocated, LossPieces(quantityIn, facts.Allocated), LossPercent(facts.QuantityIn, facts.Survivors), true);
    }

    public static AppError NotNursing(string code, string statusLabel) => Refused(
        $"Đợt ươm {code} đang ở trạng thái \"{statusLabel}\", không thể sửa hoặc hủy. Chỉ đợt đang ươm mới sửa hoặc hủy được.", NurseryReasonCodes.NotNursing);

    public static AppError NotCompleted(string code, string statusLabel) => Refused(
        $"Đợt ươm {code} đang ở trạng thái \"{statusLabel}\": chỉ đợt đã hoàn tất mới sửa số sống sót hoặc chuyển phần chia.", NurseryReasonCodes.NotCompleted);

    public static AppError Refused(string text, string reasonCode, IReadOnlyDictionary<string, object?>? details = null)
    {
        var all = new Dictionary<string, object?>(details ?? new Dictionary<string, object?>()) { ["reasonCode"] = reasonCode };
        return AppError.Conflict(UserMessages.NurseryRefused(text), all);
    }
}
