using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Cultivation.MaterialUsages;

/// <summary>CYCLE-03 reads, for a Zone Manager of the raft's zone and the Cooperative Owner. A Discontinued raft and a closed cycle keep their usage.</summary>
public sealed class MaterialUsageReadService(IMaterialUsageRepository usages, IRaftRepository rafts, MaterialUsageViews views, ZoneAccessGuard zoneAccess)
{
    public async Task<Result<MaterialUsageList>> ListAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        return await CanViewAsync(raft.ZoneId, cancellationToken) ? await views.ListAsync(raft, cancellationToken) : AppError.Forbidden;
    }

    /// <summary>One record: after a refused save (MSG06) the form shows the new version, which a move may have taken off this raft (NFR-09).</summary>
    public async Task<Result<MaterialUsageView>> GetAsync(Guid usageId, CancellationToken cancellationToken)
    {
        var usage = await usages.GetAsync(usageId, cancellationToken);
        if (usage is null)
        {
            return AppError.NotFound;
        }

        return await CanViewAsync(usage.Raft.ZoneId, cancellationToken) ? await views.RecordAsync(usageId, cancellationToken) : AppError.Forbidden;
    }

    private Task<bool> CanViewAsync(Guid zoneId, CancellationToken cancellationToken) =>
        zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.View, zoneId, cancellationToken);
}
