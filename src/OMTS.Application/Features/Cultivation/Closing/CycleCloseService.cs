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
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Closing;

/// <summary>
/// CYCLE-06 close a cultivation cycle, for a Zone Manager of the raft's zone and the Cooperative Owner. Today becomes the close date; the cycle is Closed for good and its records are
/// locked (BR-CYCLE-04): every cultivation write asks <see cref="CycleWriteGuard"/>, material usage asks <c>MaterialUsageCycleGuard</c>, and a new harvest needs an Open cycle. A new
/// cycle can start from the day after (BR-CYCLE-01). An early close needs the Cooperative Owner's approval face to face, which a Zone Manager confirms with <c>ownerApproved</c>
/// (BR-CYCLE-07). The cycle row is saved with the version the client read: a parallel seeding, log, harvest, usage, correction or cancel marks the same row, so one of the two answers
/// MSG06 and the close never saves over a record its summary did not count.
/// </summary>
public sealed class CycleCloseService(
    ICultivationCycleRepository cycles,
    CycleSummaryReader summaries,
    CycleWriteGuard writes,
    CultivationCycleViews views,
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
    public const string ClosedAction = "CultivationCycle.Closed";
    public const string AssessmentField = "Assessment";
    public const int MaxAssessmentLength = 2000;
    private const string NotificationTitle = "Cultivation cycle closed";

    /// <summary>CYCLE-06 steps 1, 2 and 4: the summary, whether the close is early and what to ask. Changes nothing.</summary>
    public async Task<Result<CycleClosePreview>> PreviewAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var (cycle, refused) = await ClosableAsync(cycleId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var summary = await summaries.ReadAsync(cycle!, cancellationToken);
        var early = summary.ClosedEarly ? UserMessages.EarlyCloseQuestion(summary.NotHarvestedLanes) : null;
        var confirm = UserMessages.CloseCycleQuestion(cycle!.Raft.Code);
        return new CycleClosePreview(
            summary,
            summary.ClosedEarly,
            summary.ClosedEarly && !await zoneAccess.IsCooperativeOwnerAsync(cancellationToken),
            early?.Text,
            early?.Code,
            confirm.Text,
            confirm.Code,
            RowVersionGuard.Encode(cycle.RowVersion));
    }

    /// <summary>CYCLE-06 steps 4 to 6: MSG-GEN-01 without an assessment, MSG-CYCLE-07 (409) for an early close a Zone Manager has not confirmed; else Closed today, audited, and the Owner told.</summary>
    public async Task<Result<CycleClosed>> CloseAsync(Guid cycleId, CloseCycleRequest request, CancellationToken cancellationToken)
    {
        var (cycle, refused) = await ClosableAsync(cycleId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var check = new FieldCheck();
        var assessment = check.Required(request.Assessment, AssessmentField, MaxAssessmentLength);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(cycle!, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        // Read after the version check: the save fails if anything changed the cycle row since, so these are the figures it closes on.
        var summary = await summaries.ReadAsync(cycle!, cancellationToken);
        var isOwner = await zoneAccess.IsCooperativeOwnerAsync(cancellationToken);
        if (summary.ClosedEarly && !isOwner && !request.OwnerApproved)
        {
            return EarlyCloseConfirmation(summary);
        }

        var today = timeProvider.Today();
        cycle!.StatusId = StatusIds.CultivationCycle.Closed;
        cycle.CloseDate = today;
        cycle.Assessment = assessment;
        cycle.ClosedEarly = summary.ClosedEarly;
        var closed = summary with { CloseDate = today, Assessment = assessment };

        audit.Record(new AuditEntry(ClosedAction, nameof(CultivationCycle))
        {
            RecordId = cycle.Id,
            RecordCode = cycle.Raft.Code,
            ZoneId = cycle.Raft.ZoneId,
            After = new
            {
                cycle.CloseDate,
                cycle.Assessment,
                cycle.ClosedEarly,
                // The Zone Manager's answer to MSG-CYCLE-07; the Owner is only asked to confirm, and a normal close asks nothing.
                OwnerApprovalConfirmed = summary.ClosedEarly && !isOwner,
                // The figures of the close day (BR-CYCLE-06): the summary itself is worked out again each time it is shown.
                Summary = new
                {
                    closed.SeedHung, closed.SeedUnit, closed.KgHarvested, closed.LastHarvestDate, closed.DaysOfFarming,
                    closed.ExpectedSeedQuantity, closed.ExpectedHarvestDate, closed.TargetYieldKg, closed.EarlyReason, closed.NotHarvestedLanes,
                },
            },
        });
        await NotifyOwnersAsync(cycle, closed, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new CycleClosed(await views.ViewAsync(cycle, cancellationToken), closed);
    }

    /// <summary>The cycle, tracked with its raft and zone, when the caller may close it and it is still Open on a raft that can be changed (MSG-GEN-09 otherwise, as for every cycle write).</summary>
    private async Task<(CultivationCycle? Cycle, AppError? Refused)> ClosableAsync(Guid cycleId, CancellationToken cancellationToken)
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

        return (cycle, writes.Refusal(cycle));
    }

    /// <summary>MSG-CYCLE-07: nothing is saved until the same request comes back with the flag (CYCLE-06.1.E1).</summary>
    private static AppError EarlyCloseConfirmation(CycleSummary summary) => AppError.Conflict(
        UserMessages.EarlyCloseQuestion(summary.NotHarvestedLanes),
        new Dictionary<string, object?>
        {
            ["confirmFlag"] = "ownerApproved",
            ["earlyReason"] = summary.EarlyReason?.ToString(),
            ["laneNumbers"] = summary.NotHarvestedLanes,
        });

    /// <summary>MSG-CYCLE-06 to the Cooperative Owners, saved with the close and pushed after it commits; an Owner who closed is not told of their own action (PM decision OMTS-216).</summary>
    private async Task NotifyOwnersAsync(CultivationCycle cycle, CycleSummary closed, CancellationToken cancellationToken)
    {
        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        var told = await ownerActorFilter.WithoutOwnerActorAsync(owners, currentUser.AccountId, cancellationToken);
        await notifications.NotifyAsync(
            told,
            new NotificationContent(
                NotificationTypes.CycleClosed,
                NotificationTitle,
                UserMessages.CycleClosedSummary(
                    closed.RaftCode, closed.AsOf, closed.ClosedEarly, closed.NotHarvestedLanes, closed.SeedHung, closed.SeedUnit ?? string.Empty, closed.KgHarvested, closed.DaysOfFarming),
                new RelatedRecord(RelatedRecordTypes.CultivationCycle, cycle.Id, cycle.Raft.Code)),
            cancellationToken);
    }
}
