using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.MaterialUsages;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// The three commands on a lane that is not as it should be (decision of 12/11/2026), for a Zone Manager of the raft's zone and the Cooperative Owner, on a raft In Use. There is no replacing,
/// no removing from the middle and no renumbering: a lane keeps its number for good.
/// <b>Report damage</b> blocks seeding and harvest on the lane, keeps its state and records why. <b>Repair</b> first checks the stock on hand of every material (MSG-INPUT-01), then in one save
/// takes them out of the stock (a use issue linked to the raft, with the usage of the raft that carries their cost), clears the flag and so returns the lane to the state it had.
/// <b>Out of service</b> ends the lane: it is left out of every list; when seed hung on it a loss event is recorded (no quantity) after a confirmation, and the lane counts as finished when the
/// cycle closes. The raft row is part of every save (stale tab: MSG06), and the cycle row too when the cycle state changes, so a parallel harvest or close never saves over it.
/// </summary>
public sealed class LaneIncidentService(
    IRaftRepository rafts,
    ILaneIncidentRepository incidents,
    ICultivationCycleRepository cycles,
    ISeedingRecordRepository seedingRecords,
    IFarmingInputItemRepository items,
    IMaterialUsageRepository usages,
    MaterialUsageCycleGuard cycleGuard,
    MaterialUsageStockSync stockSync,
    LaneStateReader laneStates,
    RaftViews raftViews,
    LaneIncidentViews views,
    LaneIncidentNotifier notifier,
    HarvestLotNotifier harvestNotifier,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string ReasonField = "Reason";
    public const string ReasonCodeField = "Reason code";
    public const string MaterialsField = "Materials";

    /// <summary>The categories a damage report or an out-of-service entry may name; optional.</summary>
    public static readonly IReadOnlySet<string> ReasonCodes = new HashSet<string>(StringComparer.Ordinal) { "LineBroken", "BuoyDamaged", "Storm", "MassMortality", "Other" };

    private sealed record Target(Raft Raft, Lane Lane, DateOnly Today, LaneDay Day, LaneState State, Guid Actor);

    public async Task<Result<LaneIncidentSaved>> ReportDamageAsync(Guid raftId, int laneNumber, ReportLaneDamageRequest request, CancellationToken cancellationToken)
    {
        var check = new FieldCheck();
        var reason = ReadReason(check, request.Reason, request.ReasonCode, out var reasonCode);
        var opened = await OpenAsync(raftId, laneNumber, request.RowVersion, check, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var (raft, lane, today, day, state, actor) = opened.Value!;
        if (lane.IsOutOfService)
        {
            return OutOfServiceRefusal(lane);
        }

        if (lane.IsDamaged)
        {
            return await RepeatedAsync(raft, lane, day, LaneIncidentKind.Damaged, cancellationToken);
        }

        var before = LaneIncidentAudit.Snapshot(lane, state.ToString());
        lane.IsDamaged = true;
        var incident = NewIncident(lane, LaneIncidentKind.Damaged, day, state, reason, reasonCode, actor, today, timeProvider.GetUtcNow());
        incidents.Add(incident);
        unitOfWork.MarkChanged(raft);
        audit.Record(LaneIncidentAudit.Entry(LaneIncidentAudit.Damaged, raft, lane, reason) with { Before = before, After = LaneIncidentAudit.Snapshot(lane, state.ToString()) });
        await notifier.DamagedAsync(raft, lane, reason, actor, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(raft, lane, incident, day, true, cycle: null, cancellationToken);
    }

    public async Task<Result<LaneIncidentSaved>> RepairAsync(Guid raftId, int laneNumber, RepairLaneRequest request, CancellationToken cancellationToken)
    {
        var check = new FieldCheck();
        var reason = ReadReason(check, request.Reason, null, out _);
        var lines = request.Materials ?? [];
        var found = (await items.GetByIdsAsync(lines.Select(l => l?.ItemId).OfType<Guid>().Distinct().ToList(), cancellationToken)).ToDictionary(i => i.Id);
        CheckMaterials(check, lines, found);
        var opened = await OpenAsync(raftId, laneNumber, request.RowVersion, check, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var (raft, lane, today, day, state, actor) = opened.Value!;
        if (lane.IsOutOfService)
        {
            return OutOfServiceRefusal(lane);
        }

        if (!lane.IsDamaged)
        {
            return AppError.Conflict(
                UserMessages.WrongStatus($"Lane {lane.LaneNumber} of raft {raft.Code}", "not damaged"),
                new Dictionary<string, object?> { ["reasonCode"] = "LANE_NOT_DAMAGED", ["laneNumber"] = lane.LaneNumber });
        }

        if (DiscontinuedLine(lines, found) is { } discontinued)
        {
            return discontinued;
        }

        if (await cycleGuard.HoldAsync(raft, today, cancellationToken) is { } closed)
        {
            return closed;
        }

        var open = await incidents.OpenDamageOfLaneAsync(lane.Id, cancellationToken);
        var incident = NewIncident(lane, LaneIncidentKind.Repaired, day, state, reason, null, actor, today, timeProvider.GetUtcNow());
        incident.ResolvesIncidentId = open?.Id;
        incidents.Add(incident);
        var usageReason = $"Lane {lane.LaneNumber}: {reason}";
        if (usageReason.Length > MaterialUsage.MaxRepairReasonLength)
        {
            usageReason = usageReason[..MaterialUsage.MaxRepairReasonLength];
        }

        foreach (var line in lines)
        {
            var item = found[line.ItemId!.Value];
            var usage = new MaterialUsage
            {
                RaftId = raft.Id, UsageDate = today, RepairReason = usageReason, ItemId = item.Id, Quantity = line.Quantity!.Value, RecordedByAccountId = actor,
            };
            if (await stockSync.PostAsync(usage, item, raft, cancellationToken) is { } stockRefused)
            {
                return stockRefused;
            }

            usages.Add(usage);
            audit.Record(MaterialUsageValues.Entry(MaterialUsageValues.Created, usage, raft) with { After = MaterialUsageValues.Snapshot(usage, raft, item) });
            var material = new LaneIncidentMaterial { IncidentId = incident.Id, MaterialUsageId = usage.Id, ItemId = item.Id, Quantity = usage.Quantity, IssueId = usage.IssueId };
            incident.Materials.Add(material);
            incidents.AddMaterial(material);
        }

        var before = LaneIncidentAudit.Snapshot(lane, state.ToString());
        lane.IsDamaged = false;
        unitOfWork.MarkChanged(raft);
        audit.Record(LaneIncidentAudit.Entry(LaneIncidentAudit.Repaired, raft, lane, reason) with { Before = before, After = LaneIncidentAudit.Snapshot(lane, state.ToString()) });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(raft, lane, incident, day, true, cycle: null, cancellationToken);
    }

    public async Task<Result<LaneIncidentSaved>> PutOutOfServiceAsync(Guid raftId, int laneNumber, LaneOutOfServiceRequest request, CancellationToken cancellationToken)
    {
        var check = new FieldCheck();
        var reason = ReadReason(check, request.Reason, request.ReasonCode, out var reasonCode);
        var opened = await OpenAsync(raftId, laneNumber, request.RowVersion, check, cancellationToken);
        if (!opened.IsSuccess)
        {
            return opened.Error!;
        }

        var (raft, lane, today, day, state, actor) = opened.Value!;
        if (lane.IsOutOfService)
        {
            return await RepeatedAsync(raft, lane, day, LaneIncidentKind.OutOfService, cancellationToken);
        }

        var hadSeed = state is LaneState.Growing or LaneState.BeingHarvested;
        var cycle = day.Cycle is { } span ? await cycles.GetForEditAsync(span.Id, cancellationToken) : null;
        if (hadSeed && !request.Confirm)
        {
            return AppError.Conflict(
                UserMessages.ConfirmAction($"put lane {lane.LaneNumber} out of service and record the loss of the seed on it"),
                new Dictionary<string, object?>
                {
                    ["confirmFlag"] = "confirm", ["laneNumber"] = lane.LaneNumber, ["state"] = state.ToString(), ["seedLotCodes"] = await SeedLotCodesAsync(cycle, lane, cancellationToken),
                });
        }

        if (cycle is not null && request.CycleRowVersion is not null && await rowVersions.CheckAsync(cycle, request.CycleRowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        var before = LaneIncidentAudit.Snapshot(lane, state.ToString());
        var open = await incidents.OpenDamageOfLaneAsync(lane.Id, cancellationToken);
        lane.OutOfServiceOn = today;
        lane.IsDamaged = false;
        var incident = NewIncident(lane, LaneIncidentKind.OutOfService, day, state, reason, reasonCode, actor, today, timeProvider.GetUtcNow());
        incident.HadSeed = hadSeed;
        incident.ResolvesIncidentId = open?.Id;
        incidents.Add(incident);
        unitOfWork.MarkChanged(raft);
        if (cycle is not null)
        {
            // The lane states of the cycle change (the lane now counts as finished): a parallel lot, seeding or close read the old ones, so one of the two answers MSG06.
            unitOfWork.MarkChanged(cycle);
        }

        var after = LaneStateRules.WithOutOfService(day.States, raft.Lanes, today);
        audit.Record(LaneIncidentAudit.Entry(LaneIncidentAudit.OutOfService, raft, lane, reason) with
        {
            Before = before, After = LaneIncidentAudit.Snapshot(lane, after[lane.Id].ToString()),
        });
        await notifier.OutOfServiceAsync(raft, lane, reason, hadSeed, actor, cancellationToken);
        var everyHarvested = LaneStateRules.EverySeededLaneHarvested(raft.Lanes.Where(l => l.StatusId == StatusIds.Lane.InUse).Select(l => after[l.Id]));
        if (cycle is not null && everyHarvested && cycle.CloseRemindedAt is null && cycle.StatusId == StatusIds.CultivationCycle.Open)
        {
            // The last lane with seed just became finished: the same reminder as the last lot gives (MSG-CYCLE-08).
            cycle.CloseRemindedAt = timeProvider.GetUtcNow();
            await harvestNotifier.CloseReminderAsync(cycle, raft, actor, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(raft, lane, incident, day, true, cycle, cancellationToken);
    }

    /// <summary>
    /// The raft, the lane In Use and the facts every command starts from, after the access check and the field checks: 404 for no such raft or lane, 403 without access, MSG-GEN-01 for
    /// an invalid field, MSG-GEN-09 for a lane Removed or a raft or zone not In Use, then the raft version (NFR-09).
    /// </summary>
    private async Task<Result<Target>> OpenAsync(Guid raftId, int laneNumber, string? rowVersion, FieldCheck check, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null || raft.Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber) is not { } lane)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (!check.IsValid)
        {
            return check.ToError();
        }

        if (lane.StatusId != StatusIds.Lane.InUse)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Lane {lane.LaneNumber} of raft {raft.Code}", statusCatalog.DisplayNameOf(lane.StatusId)));
        }

        if (raftViews.EditRefusal(raft) is { } readOnly)
        {
            return readOnly;
        }

        if (await rowVersions.CheckAsync(raft, rowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var today = timeProvider.Today();
        var day = await laneStates.ReadAsync(raft, today, cancellationToken);
        var actor = currentUser.AccountId ?? throw new InvalidOperationException("A lane command needs an authenticated caller.");
        return new Target(raft, lane, today, day, day.States[lane.Id], actor);
    }

    private static string ReadReason(FieldCheck check, string? reason, string? reasonCode, out string? code)
    {
        var text = check.Required(reason, ReasonField, LaneIncident.MaxReasonLength);
        code = string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode.Trim();
        check.Check(code is null || ReasonCodes.Contains(code), ReasonCodeField);
        return text;
    }

    private static LaneIncident NewIncident(
        Lane lane, LaneIncidentKind kind, LaneDay day, LaneState state, string reason, string? reasonCode, Guid actor, DateOnly today, DateTimeOffset now) => new()
    {
        LaneId = lane.Id,
        Lane = lane,
        CycleId = day.Cycle?.Id,
        Kind = kind,
        OccurredAt = now,
        OccurredOn = today,
        ReasonCode = reasonCode,
        Reason = reason,
        StateBefore = state.ToString(),
        RecordedByAccountId = actor,
    };

    private AppError OutOfServiceRefusal(Lane lane) =>
        AppError.Conflict(
            UserMessages.WrongStatus($"Lane {lane.LaneNumber}", "out of service"),
            new Dictionary<string, object?> { ["reasonCode"] = LaneReasonCodes.OutOfService, ["laneNumber"] = lane.LaneNumber });

    /// <summary>The call is repeated: the lane is already in that condition, nothing is saved and the entry that put it there is returned (200, <c>changed: false</c>).</summary>
    private async Task<Result<LaneIncidentSaved>> RepeatedAsync(Raft raft, Lane lane, LaneDay day, LaneIncidentKind kind, CancellationToken cancellationToken)
    {
        var latest = (await incidents.ListOfRaftAsync(raft.Id, lane.Id, cancellationToken)).FirstOrDefault(i => i.Kind == kind);
        return latest is null
            ? AppError.NotFound
            : await SavedAsync(raft, lane, latest, day, false, cycle: null, cancellationToken);
    }

    private async Task<LaneIncidentSaved> SavedAsync(
        Raft raft, Lane lane, LaneIncident incident, LaneDay day, bool changed, Domain.Entities.Cultivation.CultivationCycle? cycle, CancellationToken cancellationToken)
    {
        var states = LaneStateRules.WithOutOfService(day.States, raft.Lanes, timeProvider.Today());
        var saved = UserMessages.Saved("Lane incident");
        return new LaneIncidentSaved(
            await views.ViewAsync(incident, cancellationToken),
            raftViews.LanesOn(raft, states).Single(l => l.Id == lane.Id),
            changed,
            RowVersionGuard.Encode(raft.RowVersion),
            cycle is null ? null : RowVersionGuard.Encode(cycle.RowVersion),
            LaneStateRules.EverySeededLaneHarvested(raft.Lanes.Where(l => l.StatusId == StatusIds.Lane.InUse).Select(l => states[l.Id])),
            saved.Text,
            saved.Code);
    }

    /// <summary>The codes of the seed lots hung on the lane in the cycle, for the question before its loss is recorded.</summary>
    private async Task<IReadOnlyList<string>> SeedLotCodesAsync(Domain.Entities.Cultivation.CultivationCycle? cycle, Lane lane, CancellationToken cancellationToken) =>
        cycle is null
            ? []
            : (await seedingRecords.ListByCycleAsync(cycle.Id, cancellationToken))
                .Where(s => s.Lanes.Any(l => l.LaneId == lane.Id))
                .Select(s => SeedingValues.LotCode(s.SeedLotLine))
                .Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Each item of the group Material and only once, a quantity above 0 as the usage records take it, and a unit that is the unit of the item when one is sent.</summary>
    private static void CheckMaterials(FieldCheck check, IReadOnlyList<RepairMaterialInput> lines, Dictionary<Guid, FarmingInputItem> found)
    {
        var seen = new HashSet<Guid>();
        for (var i = 0; i < lines.Count; i++)
        {
            var item = lines[i]?.ItemId is { } id ? found.GetValueOrDefault(id) : null;
            check.Check(MaterialUsageValues.IsMaterial(item) && seen.Add(item!.Id), MaterialUsageValues.LineItemField(i));
            MaterialUsageValues.CheckQuantity(check, lines[i]?.Quantity, MaterialUsageValues.LineQuantityField(i));
            check.Check(
                string.IsNullOrWhiteSpace(lines[i]?.Unit) || item is null || string.Equals(lines[i]!.Unit!.Trim(), item.Unit, StringComparison.OrdinalIgnoreCase),
                $"Line {i + 1} unit");
        }

        check.Check(lines.Count <= 50, MaterialsField);
    }

    private AppError? DiscontinuedLine(IReadOnlyList<RepairMaterialInput> lines, Dictionary<Guid, FarmingInputItem> found)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var item = found[lines[i].ItemId!.Value];
            if (MaterialUsageValues.IsDiscontinued(item))
            {
                return AppError.NotUsableChoice($"Input item {item.Code}", statusCatalog.DisplayNameOf(item.StatusId), MaterialUsageValues.LineItemField(i));
            }
        }

        return null;
    }
}
