using System.Text.Json.Nodes;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Traceability.Corrections;

/// <summary>
/// TRACE-03.0 correct a seeding session that only this use case may still change: one of a Closed cycle, or one with a lane in a harvest lot (BR-TRACE-04). The date, seed lot,
/// lanes and quantity change with the rules of CYCLE-02 and a reason; the session is never removed. Every lane of every lot of the cycle keeps a seed lot (MSG-TRACE-03). A Zone
/// Manager confirms the Owner's agreement (MSG-TRACE-04); the Owner only confirms. The provenance pages read the current data, so the Published lots it changes show it at once,
/// stay Published (a Withdrawn lot stays Withdrawn), and the audit row names them (BR-TRACE-05). The session carries a version (NFR-09); the cycle row and the receipt row of the
/// seed lot are part of the save, so a parallel close, correction or purchase return answers MSG06 on one side.
/// </summary>
public sealed class TraceSeedingCorrectionService(
    ISeedingRecordRepository records,
    IHarvestLotRepository harvestLots,
    IRaftRepository rafts,
    TraceCorrectionGuard guard,
    TraceSeedingReader reader,
    SeedingViews views,
    LaneStateReader laneStates,
    RowVersionGuard rowVersions,
    Cultivation.Seeding.SeedingStockSync stockSync,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private sealed record Plan(Raft Raft, SeedingFields Fields, object Before, object After, JsonObject? ChangedBefore, JsonObject? ChangedAfter, IReadOnlyList<HarvestLot> Affected, bool IsOwner);

    /// <summary>Steps 5 and 6: the checks of the save, the values before and after, and the lots whose origin information changes. Changes nothing.</summary>
    public async Task<Result<TraceCorrectionPreview>> PreviewAsync(Guid recordId, TraceSeedingCorrectionRequest request, CancellationToken cancellationToken)
    {
        var record = await records.GetAsync(recordId, cancellationToken);
        if (record is null)
        {
            return AppError.NotFound;
        }

        var planned = await PlanAsync(record, request, cancellationToken);
        if (!planned.IsSuccess)
        {
            return planned.Error!;
        }

        var plan = planned.Value!;
        return TraceCorrectionAnswers.Preview(
            SeedingValues.RecordType, record.Id, plan.Before, plan.After, plan.ChangedAfter, plan.Affected, plan.IsOwner, RowVersionGuard.Encode(record.RowVersion));
    }

    /// <summary>Steps 7 and 8. A correction that changes nothing saves nothing and writes no audit row.</summary>
    public async Task<Result<TraceSeedingCorrected>> CorrectAsync(Guid recordId, TraceSeedingCorrectionRequest request, CancellationToken cancellationToken)
    {
        var record = await records.GetForEditAsync(recordId, cancellationToken);
        if (record is null)
        {
            return AppError.NotFound;
        }

        var planned = await PlanAsync(record, request, cancellationToken);
        if (!planned.IsSuccess)
        {
            return planned.Error!;
        }

        var plan = planned.Value!;
        if (await rowVersions.CheckAsync(record, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (!plan.IsOwner && !request.OwnerApproved)
        {
            return TraceCorrectionAnswers.AgreementRequired(plan.Affected);
        }

        var published = TraceCorrectionAnswers.PublishedCodes(plan.Affected);
        if (plan.ChangedAfter is not null)
        {
            if (await stockSync.ReplaceAsync(record, plan.Fields, plan.Raft, cancellationToken) is { } stockRefused)
            {
                return stockRefused;
            }

            SeedingRecordRows.Apply(records, record, plan.Fields);
            // Lanes are rows of their own: a change of lanes only must still give the session a new version.
            unitOfWork.MarkChanged(record);
            unitOfWork.MarkChanged(record.Cycle);
            unitOfWork.MarkChanged(plan.Fields.Lot.Receipt);
            audit.Record(SeedingValues.Entry(TraceCorrectionAudit.SeedingRecordCorrected, record) with
            {
                Before = plan.ChangedBefore, After = TraceCorrectionAudit.After(plan.ChangedAfter, published, !plan.IsOwner), Reason = plan.Fields.Reason,
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var states = (await laneStates.ReadAsync(plan.Raft, timeProvider.Today(), cancellationToken)).States;
        var saved = UserMessages.Saved("Seeding session");
        return new TraceSeedingCorrected(await views.ViewAsync(record, states, cancellationToken), published, saved.Text, saved.Code);
    }

    /// <summary>Access, the correctable set, the seeding rules, the seed lot of every lane of every lot, and what changes. The session comes with its cycle, raft and zone.</summary>
    private async Task<Result<Plan>> PlanAsync(SeedingRecord record, TraceSeedingCorrectionRequest request, CancellationToken cancellationToken)
    {
        var cycle = record.Cycle;
        if (!await guard.MayCorrectAsync(cycle.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var lots = await harvestLots.ListByCycleAsync(cycle.Id, cancellationToken);
        if ((NotCorrectableHere(record, lots) ?? guard.RaftRefusal(cycle.Raft)) is { } refused)
        {
            return refused;
        }

        var raft = await rafts.GetForEditAsync(cycle.RaftId, cancellationToken) ?? cycle.Raft;
        var ofCycle = await records.ListByCycleAsync(cycle.Id, cancellationToken);
        var (fields, invalid) = await reader.ReadAsync(record, raft, request, ofCycle, lots, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var lanesBefore = record.Lanes.Select(l => l.LaneId).ToHashSet();
        var lanesAfter = fields!.Lanes.Select(l => l.Id).ToHashSet();
        var otherLanes = ofCycle.Where(s => s.Id != record.Id).SelectMany(s => s.Lanes.Select(l => l.LaneId)).ToHashSet();
        var lotLanes = TraceSeedLotRules.LanesOf(lots);
        var lost = TraceSeedLotRules.LanesLosingTheirSeed(lotLanes, otherLanes.Union(lanesBefore).ToHashSet(), lotLanes, otherLanes.Union(lanesAfter).ToHashSet());
        if (lost.Count > 0)
        {
            return TraceSeedLotRules.Refusal(lost);
        }

        var before = SeedingValues.Snapshot(record);
        var after = Snapshot(fields);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, after);
        var dateOrSeedLotChanged = fields.Date != record.SeedingDate || fields.Lot.Id != record.SeedLotLineId;
        var affected = changedAfter is null ? [] : TraceSeedLotRules.LotsHolding(lots, TraceSeedLotRules.LanesWhoseOriginChanges(lanesBefore, lanesAfter, dateOrSeedLotChanged));
        return new Plan(raft, fields, before, after, changedBefore, changedAfter, affected, await guard.IsOwnerAsync(cancellationToken));
    }

    /// <summary>
    /// BR-TRACE-04: a session of a Closed cycle, or of an Open one with a lane in a harvest lot of the cycle. Any other session is still corrected in CYCLE-02.2, so MSG-GEN-09.
    /// </summary>
    private static AppError? NotCorrectableHere(SeedingRecord record, IReadOnlyList<HarvestLot> lots)
    {
        var lanes = record.Lanes.Select(l => l.LaneId).ToHashSet();
        return record.Cycle.StatusId == StatusIds.CultivationCycle.Closed || TraceSeedLotRules.LotsHolding(lots, lanes).Count > 0
            ? null
            : AppError.Conflict(UserMessages.WrongStatus($"The seeding session of raft {record.Cycle.Raft.Code} on {record.SeedingDate:dd/MM/yyyy}", "not in a harvest lot"));
    }

    /// <summary>The corrected session with the values <see cref="SeedingValues.Snapshot"/> keeps, before it is applied.</summary>
    private static object Snapshot(SeedingFields fields) => new
    {
        SeedingDate = fields.Date,
        SeedLot = SeedingValues.LotCode(fields.Lot),
        Lanes = fields.Lanes.Select(l => l.LaneNumber).Order().ToList(),
        fields.Quantity,
    };
}
