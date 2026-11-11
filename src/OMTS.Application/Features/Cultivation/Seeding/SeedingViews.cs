using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>The lanes In Use and how many have seed (BR-CYCLE-02); the source of MSG-CYCLE-01 and MSG-CYCLE-02.</summary>
public sealed record SeedingFigures(int LaneCount, int SeededCount, IReadOnlyList<int> NotSeededLanes);

/// <summary>The session answers, with the commands the caller has. Every caller has already passed the zone check on the record.</summary>
public sealed class SeedingViews(
    IAccountRepository accounts,
    PermissionService permissions,
    ICurrentUser currentUser)
{
    /// <summary>BR-CYCLE-02: the Cooperative Owner corrects any session, a Zone Manager of the zone only one they recorded. Whether the cycle still allows it is asked apart.</summary>
    public static bool MayCorrect(AccountAccess? access, SeedingRecord record, Guid zoneId) =>
        access is not null
        && access.Allows(PermissionFunctions.CultivationCycles, PermissionAction.Edit, PermissionScope.ZoneScoped, zoneId)
        && (access.IsCooperativeOwner || record.RecordedByAccountId == access.AccountId);

    public static SeedingFigures Figures(Raft raft, IReadOnlyDictionary<Guid, LaneState> states)
    {
        var inUse = raft.Lanes.Where(l => l.StatusId == StatusIds.Lane.InUse).OrderBy(l => l.LaneNumber).ToList();
        var notSeeded = inUse.Where(l => states[l.Id] == LaneState.NotSeeded).Select(l => l.LaneNumber).ToList();
        return new SeedingFigures(inUse.Count, inUse.Count - notSeeded.Count, notSeeded);
    }

    public static SeedLotRef LotRef(PurchaseReceiptLine lot) => new(
        lot.Id, SeedingValues.LotCode(lot), lot.ReceiptId, lot.Receipt.Code, lot.Receipt.ReceiptDate, lot.ItemId, lot.Item?.Code, lot.Item?.Name, lot.Item?.Unit ?? lot.Unit, lot.Receipt.SupplierId, lot.Receipt.Supplier.Name);

    public async Task<AccountAccess?> AccessAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId ? await permissions.GetAccessAsync(accountId, cancellationToken) : null;

    /// <summary>One session, with its lanes, cycle, raft, zone and seed lot.</summary>
    /// <param name="states">The lanes of the raft today.</param>
    public async Task<SeedingRecordView> ViewAsync(SeedingRecord record, IReadOnlyDictionary<Guid, LaneState> states, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync([record.RecordedByAccountId], cancellationToken);
        return View(record, record.Cycle, names.GetValueOrDefault(record.RecordedByAccountId), await AccessAsync(cancellationToken), states);
    }

    /// <summary>The sessions of the cycle, which comes with its raft and zone, as they were read.</summary>
    public async Task<IReadOnlyList<SeedingRecordView>> ListAsync(
        IReadOnlyList<SeedingRecord> records, CultivationCycle cycle, IReadOnlyDictionary<Guid, LaneState> states, CancellationToken cancellationToken)
    {
        var names = await accounts.GetDisplayNamesAsync(records.Select(r => r.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        var access = await AccessAsync(cancellationToken);
        return records.Select(r => View(r, cycle, names.GetValueOrDefault(r.RecordedByAccountId), access, states)).ToList();
    }

    private static SeedingRecordView View(
        SeedingRecord record, CultivationCycle cycle, string? recordedBy, AccountAccess? access, IReadOnlyDictionary<Guid, LaneState> states)
    {
        var raft = cycle.Raft;
        var inALot = record.Lanes.Any(l => states.TryGetValue(l.LaneId, out var state) && SeedingLaneRules.IsInAHarvestLot(state));
        return new SeedingRecordView(
            record.Id,
            cycle.Id,
            raft.Id,
            raft.Code,
            record.SeedingDate,
            LotRef(record.SeedLotLine),
            record.Quantity,
            record.Lanes.Select(l => l.Lane.LaneNumber).Order().ToList(),
            record.RecordedByAccountId,
            recordedBy,
            record.CreatedAt,
            CultivationCycleViews.CanEdit(cycle, raft, raft.Zone.StatusId == StatusIds.Usage.InUse) && !inALot && MayCorrect(access, record, raft.ZoneId),
            RowVersionGuard.Encode(record.RowVersion));
    }

    /// <summary>MSG-GEN-05 "Seeding session saved.", and the offer to mark the seeding complete when no lane is left Not seeded (CYCLE-02.0 step 7).</summary>
    public async Task<SeedingRecordSaved> SavedAsync(SeedingRecord record, Raft raft, LaneStateReader laneStates, DateOnly today, CancellationToken cancellationToken)
    {
        var states = (await laneStates.ReadAsync(raft, today, cancellationToken)).States;
        var saved = UserMessages.Saved("Seeding session");
        return new SeedingRecordSaved(
            await ViewAsync(record, states, cancellationToken),
            record.Cycle.SeedingCompletedOn is null && Figures(raft, states).NotSeededLanes.Count == 0,
            RowVersionGuard.Encode(record.Cycle.RowVersion),
            saved.Text,
            saved.Code);
    }
}

