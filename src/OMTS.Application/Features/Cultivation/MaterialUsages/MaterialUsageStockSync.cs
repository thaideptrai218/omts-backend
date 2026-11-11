using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Purchasing.Issues;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Cultivation.MaterialUsages;

/// <summary>
/// A material usage and its stock issue (Xuất dùng, raft repair) are one fact (PM decision 11/11/2026): the same save writes or changes the linked issue, and MSG-INPUT-01 (409) refuses a quantity
/// above what is on hand. The issue names the usage, the raft and the cycle open on the date (none when the date is in no cycle). A usage recorded before this has no issue (legacy): it never left
/// the stock and stays so. Nothing is saved here: the callers save the usage and the issue together.
/// </summary>
public sealed class MaterialUsageStockSync(FarmingStockIssuer issuer, IInputIssueRepository issues, ICultivationCycleQuery cycleQuery)
{
    /// <summary>A new usage: writes the issue and links it.</summary>
    internal async Task<AppError?> PostAsync(MaterialUsage usage, FarmingInputItem item, Raft raft, CancellationToken cancellationToken)
    {
        var issue = await issuer.PostAsync(
            InputIssueSource.MaterialUsage, usage.Id, raft, await CycleOnAsync(raft, usage.UsageDate, cancellationToken), usage.UsageDate,
            [new FarmingIssueLine(item, null, usage.Quantity)], InputIssueTypeIds.RaftRepair, cancellationToken);
        (usage.IssueId, usage.Issue) = (issue.Id, issue);
        return await issuer.CheckNotNegativeAsync(cancellationToken);
    }

    /// <summary>An edited usage (with its new values): the issue follows the item, raft, date and quantity. A legacy usage has no issue and changes alone.</summary>
    internal async Task<AppError?> ReplaceAsync(MaterialUsage usage, FarmingInputItem item, Raft raft, CancellationToken cancellationToken)
    {
        if (usage.IssueId is not { } issueId || await issues.GetForEditAsync(issueId, cancellationToken) is not { } issue)
        {
            return null;
        }

        await issuer.ReplaceAsync(
            issue, raft, await CycleOnAsync(raft, usage.UsageDate, cancellationToken), usage.UsageDate, [new FarmingIssueLine(item, null, usage.Quantity)], cancellationToken);
        return await issuer.CheckNotNegativeAsync(cancellationToken);
    }

    private async Task<Guid?> CycleOnAsync(Raft raft, DateOnly date, CancellationToken cancellationToken) =>
        await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken) is { } open && open.StartDate <= date ? open.Id : null;
}
