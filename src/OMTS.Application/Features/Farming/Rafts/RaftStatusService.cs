using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>FARM-04.3 Discontinue and FARM-04.4 Resume, Cooperative Owner only (API filter). Lanes and photos are kept as they are.</summary>
public sealed class RaftStatusService(IRaftRepository rafts, ICultivationCycleQuery cycles, RaftViews views, StatusChangeService statusChanges, IUnitOfWork unitOfWork)
{
    /// <summary>In Use → Discontinued, only without an open cultivation cycle.</summary>
    public async Task<Result<RaftView>> DiscontinueAsync(Guid raftId, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (raft.StatusId != StatusIds.Usage.InUse)
        {
            return WrongStatus(raft);
        }

        // FARM-04.3.E1: MSG-GEN-06 naming the open cycle (MSG61 before).
        if (await cycles.OpenCycleAsync(raft.Id, cancellationToken) is { } openCycle)
        {
            return OpenCycle(openCycle, MessageCodes.Legacy.Msg61);
        }

        return await ChangeStatusAsync(raft, StatusIds.Usage.Discontinued, "Raft.Discontinued", request, cancellationToken);
    }

    /// <summary>Discontinued → In Use with the same code, lanes and history, only when the zone is In Use.</summary>
    public async Task<Result<RaftView>> ReactivateAsync(Guid raftId, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (raft.StatusId != StatusIds.Usage.Discontinued)
        {
            return WrongStatus(raft);
        }

        // As on create: the zone row is part of this save, so a parallel zone discontinue fails with MSG06.
        unitOfWork.MarkChanged(raft.Zone);
        return raft.Zone.StatusId == StatusIds.Usage.InUse
            ? await ChangeStatusAsync(raft, StatusIds.Usage.InUse, "Raft.Reactivated", request, cancellationToken)
            : statusChanges.WrongStatus($"Farming zone {raft.Zone.Code}", raft.Zone);
    }

    /// <summary>MSG-GEN-06 naming the open cultivation cycle, with its <c>cycleId</c>.</summary>
    internal static AppError OpenCycle(CycleSpan cycle, string legacyCode = MessageCodes.Legacy.Msg11)
    {
        var name = $"cultivation cycle started {cycle.StartDate:dd/MM/yyyy}";
        return AppError.Conflict(UserMessages.StillInUse(1, name) with { LegacyCode = legacyCode }, new Dictionary<string, object?> { ["cycleId"] = cycle.Id });
    }

    private async Task<Result<RaftView>> ChangeStatusAsync(Raft raft, int newStatusId, string action, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var entry = new AuditEntry(action, nameof(Raft)) { RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId };
        return await statusChanges.ChangeAsync(raft, newStatusId, entry, request.Reason, request.RowVersion, cancellationToken) is { } error
            ? error
            : await views.ViewAsync(raft, cancellationToken);
    }

    private AppError WrongStatus(Raft raft) => statusChanges.WrongStatus($"Raft {raft.Code}", raft);
}
