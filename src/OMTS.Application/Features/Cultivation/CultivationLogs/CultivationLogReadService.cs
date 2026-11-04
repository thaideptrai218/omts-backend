using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>CYCLE-04 and CYCLE-05 log reads, for a Zone Manager of the raft's zone and the Cooperative Owner; a closed cycle keeps its logs.</summary>
public sealed class CultivationLogReadService(
    ICultivationCycleRepository cycles, ICultivationLogRepository logs, CultivationLogViews views, ZoneAccessGuard zoneAccess)
{
    public async Task<Result<IReadOnlyList<CultivationLogView>>> ListAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        if (!await CanViewAsync(cycle.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        return Result<IReadOnlyList<CultivationLogView>>.Success(await views.ListAsync(cycle, cancellationToken));
    }

    public async Task<Result<CultivationLogView>> GetAsync(Guid logId, CancellationToken cancellationToken)
    {
        var log = await logs.GetAsync(logId, cancellationToken);
        if (log is null)
        {
            return AppError.NotFound;
        }

        return await CanViewAsync(log.Cycle.Raft.ZoneId, cancellationToken) ? await views.ViewAsync(log, cancellationToken) : AppError.Forbidden;
    }

    private Task<bool> CanViewAsync(Guid zoneId, CancellationToken cancellationToken) =>
        zoneAccess.AllowsAsync(PermissionFunctions.CultivationLogs, PermissionAction.View, zoneId, cancellationToken);
}
