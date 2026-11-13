using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>The refusals of the nursery, each with a <c>reasonCode</c> a client can tell apart (MSG-GEN-09 until the SRS has MSG-NURSERY-xx).</summary>
public static class NurseryReasonCodes
{
    public const string NotNursing = "NURSERY_NOT_NURSING";
    public const string NotCompleted = "NURSERY_NOT_COMPLETED";
    public const string RaftOtherZone = "NURSERY_RAFT_OTHER_ZONE";
    public const string RaftNotUsable = "NURSERY_RAFT_NOT_USABLE";
    public const string SurvivorsBelowSeeded = "NURSERY_SURVIVORS_BELOW_SEEDED";

    /// <summary>Seeding (or a correction of a seeding record) would take more pieces than are left in the pool of the batch (v2, 13/11/2026).</summary>
    public const string PoolExceeded = "NURSERY_POOL_EXCEEDED";

    /// <summary>A cycle cannot start from a batch with nothing left in its pool.</summary>
    public const string PoolEmpty = "NURSERY_POOL_EMPTY";

    /// <summary>Close refused: nothing is left in the pool.</summary>
    public const string NothingToClose = "NURSERY_NOTHING_TO_CLOSE";

    /// <summary>The transfer of shares between rafts is gone (v2): 410.</summary>
    public const string AllocationsRemoved = "NURSERY_ALLOCATIONS_REMOVED";

    /// <summary>The nursery batch of a cycle changes only before its first seeding record (409).</summary>
    public const string CycleSeedLocked = "CYCLE_SEED_LOCKED";

    /// <summary>A seeding record of a nursery cycle no longer changes once the seeding of the cycle is marked complete (409).</summary>
    public const string SeedingCompleted = "SEEDING_COMPLETED";
}

/// <summary>The figures and checks of the nursery as pure functions: the loss, the pool of a batch and the cap of seeding from it.</summary>
public static class NurseryRules
{
    /// <summary>Pieces put in less survivors; an estimate, as the survivors are counted by eye.</summary>
    public static decimal LossPieces(decimal quantityIn, decimal survivors) => quantityIn - survivors;

    /// <summary>The loss as a percentage of the pieces put in with one decimal; 0 when nothing was put in.</summary>
    public static decimal LossPercent(decimal quantityIn, decimal survivors) =>
        quantityIn > 0 ? decimal.Round((quantityIn - survivors) / quantityIn * 100, 1) : 0m;

    /// <summary>The whole estimated loss of a batch: put in less survivors, plus what its close wrote off.</summary>
    public static decimal TotalLoss(decimal quantityIn, decimal survivors, decimal? closeLoss) => quantityIn - survivors + (closeLoss ?? 0m);

    /// <summary>The total loss as a percentage of the pieces put in with one decimal; 0 when nothing was put in.</summary>
    public static decimal TotalLossPercent(decimal quantityIn, decimal survivors, decimal? closeLoss) =>
        quantityIn > 0 ? decimal.Round(TotalLoss(quantityIn, survivors, closeLoss) / quantityIn * 100, 1) : 0m;

    /// <summary>A quantity of pieces as the forms take it: above 0, at most two decimals (as the receipts), within the limit of the receipts.</summary>
    public static bool IsPieces(decimal? quantity) =>
        quantity is > 0 and <= PurchaseReceiptValidator.MaxQuantity && PurchaseReceiptValidator.HasAllowedDecimals(quantity.Value);

    /// <summary>The survivors: from 0 to the pieces put in, at most two decimals.</summary>
    public static bool IsSurvivors(decimal? survivors, decimal quantityIn) =>
        survivors is >= 0 && survivors <= quantityIn && PurchaseReceiptValidator.HasAllowedDecimals(survivors.Value);

    /// <summary>What is left in the pool of a batch that is Completed: the survivors less what every cycle seeded, never below 0. A Closed batch has nothing left (it was written off); the others have no pool (null).</summary>
    public static decimal? PoolRemaining(NurseryBatch batch, decimal seeded) => batch.StatusId switch
    {
        StatusIds.NurseryBatch.Completed => Math.Max(0m, (batch.Survivors ?? 0m) - seeded),
        StatusIds.NurseryBatch.Closed => 0m,
        _ => null,
    };

    /// <summary>What can still be hung from a pool: the survivors less what is hung, never below 0.</summary>
    public static decimal Remaining(decimal survivors, decimal seeded) => Math.Max(0m, survivors - seeded);

    /// <summary>The pieces the pool can ever give: the survivors, less what a close wrote off (the pieces seeded before the close). Records of a Closed batch can be kept or lowered but not raised.</summary>
    public static decimal PoolSize(NurseryBatch batch) => (batch.Survivors ?? 0m) - (batch.CloseLossPieces ?? 0m);

    /// <summary>
    /// The hard cap of seeding from a nursery (v2): the quantity of the session may not pass what is left in the pool, counted without the session itself when it is being corrected
    /// (<paramref name="seededByOthers"/> excludes it). Null when within it; else 409 NURSERY_POOL_EXCEEDED with the figures.
    /// </summary>
    public static AppError? CheckPool(NurseryBatch batch, decimal seededByOthers, decimal quantity)
    {
        var survivors = PoolSize(batch);
        var remaining = Remaining(survivors, seededByOthers);
        if (quantity <= remaining)
        {
            return null;
        }

        return AppError.Conflict(
            UserMessages.NurseryRefused($"Kho ươm {batch.Code} chỉ còn {remaining:0.###} mảnh, không thể thả {quantity:0.###} mảnh."),
            new Dictionary<string, object?>
            {
                ["reasonCode"] = NurseryReasonCodes.PoolExceeded, ["field"] = "Quantity", ["survivors"] = survivors, ["seeded"] = seededByOthers, ["remaining"] = remaining, ["quantity"] = quantity,
            });
    }
    /// <summary>
    /// The nursery block of a cycle: the figures of the whole batch shared by what the cycle seeded (a batch can feed several cycles): share = seeded by the cycle / seeded by the batch, 0 when the batch has
    /// no seeding. The loss includes what a close of the batch wrote off; all of it an estimate.
    /// </summary>
    public static NurserySummary SummaryOf(NurseryCycleFacts facts)
    {
        var share = facts.BatchSeeded > 0 ? facts.CycleSeeded / facts.BatchSeeded : 0m;
        return new NurserySummary(
            facts.BatchId, facts.BatchCode, decimal.Round(facts.QuantityIn * share, 3), decimal.Round(facts.Survivors * share, 3),
            decimal.Round(TotalLoss(facts.QuantityIn, facts.Survivors, facts.CloseLoss) * share, 3), TotalLossPercent(facts.QuantityIn, facts.Survivors, facts.CloseLoss), true);
    }

    /// <summary>409 SEEDING_COMPLETED: a seeding record of a cycle that draws from a nursery is not added, corrected or removed once the seeding of the cycle is marked complete (v2).</summary>
    public static AppError SeedingCompletedRefusal(string raftCode) => Refused(
        $"Chu kỳ của bè {raftCode} đã hoàn tất thả giống, không thể thêm, sửa hoặc xóa bản ghi thả giống.", NurseryReasonCodes.SeedingCompleted);

    public static AppError NotNursing(string code, string statusLabel) => Refused(
        $"Đợt ươm {code} đang ở trạng thái \"{statusLabel}\", không thể sửa hoặc hủy. Chỉ đợt đang ươm mới sửa hoặc hủy được.", NurseryReasonCodes.NotNursing);

    public static AppError NotCompleted(string code, string statusLabel) => Refused(
        $"Đợt ươm {code} đang ở trạng thái \"{statusLabel}\": chỉ đợt đã ươm xong mới sửa số sống sót, đóng đợt hoặc dùng để nuôi.", NurseryReasonCodes.NotCompleted);

    public static AppError Refused(string text, string reasonCode, IReadOnlyDictionary<string, object?>? details = null)
    {
        var all = new Dictionary<string, object?>(details ?? new Dictionary<string, object?>()) { ["reasonCode"] = reasonCode };
        return AppError.Conflict(UserMessages.NurseryRefused(text), all);
    }
}
