using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>
/// CYCLE-02.1 mark the seeding complete, one time per cycle, also with lanes still Not seeded after a confirmation that lists them (MSG-CYCLE-01). The Cooperative Owners are told once
/// to review the seeding, in the same save (MSG-CYCLE-02), except the Owner who marked it (PM decision OMTS-216). A second mark, in sequence or in parallel, tells nobody.
/// </summary>
public sealed class SeedingCompletionService(
    ICultivationCycleRepository cycles,
    IRaftRepository rafts,
    CycleWriteGuard writes,
    LaneStateReader laneStates,
    CultivationCycleViews cycleViews,
    ZoneAccessGuard zoneAccess,
    IZoneRecipientQuery recipients,
    OwnerActorRecipientFilter ownerActorFilter,
    INotificationDispatcher notifications,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private const string NotificationTitle = "Seeding finished";

    /// <summary>CYCLE-02.1 step 2: the question to ask, and the lanes it lists.</summary>
    public async Task<Result<SeedingCompletionPreview>> PreviewAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var (cycle, refused) = await MarkableAsync(cycleId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var (_, figures) = await FiguresAsync(cycle!, cancellationToken);
        var question = figures.SeededCount == 0 ? UserMessages.FinishEmptySeedingQuestion(cycle!.Raft.Code) : UserMessages.FinishSeedingQuestion(cycle!.Raft.Code, figures.NotSeededLanes);
        return new SeedingCompletionPreview(
            cycle.Id, cycle.Raft.Code, figures.LaneCount, figures.SeededCount, figures.NotSeededLanes, question.Text, question.Code, RowVersionGuard.Encode(cycle.RowVersion));
    }

    public async Task<Result<SeedingCompleted>> CompleteAsync(Guid cycleId, MarkSeedingCompleteRequest request, CancellationToken cancellationToken)
    {
        var (cycle, refused) = await MarkableAsync(cycleId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        if (await rowVersions.CheckAsync(cycle!, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var (raft, figures) = await FiguresAsync(cycle!, cancellationToken);
        if (figures.SeededCount == 0 && !request.ConfirmEmpty)
        {
            // PM decision 11/11/2026: finishing the seeding of a raft with no seeded lane needs a confirmation (MSG-CYCLE-01 style); nothing is saved until it comes back with the flag.
            return AppError.Conflict(
                UserMessages.FinishEmptySeedingQuestion(raft.Code),
                new Dictionary<string, object?> { ["confirmFlag"] = "confirmEmpty", ["laneNumbers"] = figures.NotSeededLanes });
        }

        cycle!.SeedingCompletedOn = timeProvider.Today();
        audit.Record(new AuditEntry(SeedingValues.CycleCompleted, nameof(CultivationCycle))
        {
            RecordId = cycle.Id,
            RecordCode = raft.Code,
            ZoneId = raft.ZoneId,
            After = new { cycle.SeedingCompletedOn, figures.SeededCount, figures.LaneCount },
        });
        await NotifyOwnersAsync(cycle, raft, figures, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SeedingCompleted(await cycleViews.ViewAsync(cycle, cancellationToken), figures.LaneCount, figures.SeededCount, figures.NotSeededLanes);
    }

    /// <summary>The cycle, when the caller may edit it, it can be changed, and its seeding is not marked complete yet (MSG-GEN-09: one time per cycle).</summary>
    private async Task<(CultivationCycle? Cycle, AppError? Refused)> MarkableAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetForEditAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return (null, AppError.NotFound);
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, cycle.Raft.ZoneId, cancellationToken))
        {
            return (cycle, AppError.Forbidden);
        }

        return (cycle, writes.Refusal(cycle) ?? (cycle.SeedingCompletedOn is { } on
            ? AppError.Conflict(UserMessages.WrongStatus($"The seeding of raft {cycle.Raft.Code}", $"marked complete on {on:dd/MM/yyyy}"))
            : null));
    }

    private async Task<(Raft Raft, SeedingFigures Figures)> FiguresAsync(CultivationCycle cycle, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(cycle.RaftId, cancellationToken) ?? cycle.Raft;
        return (raft, SeedingViews.Figures(raft, (await laneStates.ReadAsync(raft, timeProvider.Today(), cancellationToken)).States));
    }

    private async Task NotifyOwnersAsync(CultivationCycle cycle, Raft raft, SeedingFigures figures, CancellationToken cancellationToken)
    {
        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        var told = await ownerActorFilter.WithoutOwnerActorAsync(owners, currentUser.AccountId, cancellationToken);
        await notifications.NotifyAsync(
            told,
            new NotificationContent(
                NotificationTypes.CycleSeedingCompleted,
                NotificationTitle,
                UserMessages.SeedingFinished(raft.Code, figures.SeededCount, figures.LaneCount),
                new RelatedRecord(RelatedRecordTypes.CultivationCycle, cycle.Id, raft.Code)),
            cancellationToken);
    }
}
