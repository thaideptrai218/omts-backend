using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>BR-CYCLE-02 on the state of a lane, as pure functions. The state is the one of today, the stricter reading (PM default).</summary>
public static class SeedingLaneRules
{
    /// <summary>Before the seeding is marked complete, a lane Not seeded or Growing (re-hanging); after it, only a lane still Not seeded. Never a lane in a harvest lot.</summary>
    public static bool CanBeSeeded(LaneState state, bool seedingCompleted) =>
        state == LaneState.NotSeeded || (state == LaneState.Growing && !seedingCompleted);

    /// <summary>A session with such a lane is corrected only in TRACE-03 (BR-CYCLE-02).</summary>
    public static bool IsInAHarvestLot(LaneState state) => state is LaneState.BeingHarvested or LaneState.Harvested;
}

/// <summary>The fields of a session (MSG-GEN-01 names them), its seed lot code and its audit rows (GBR-02).</summary>
public static class SeedingValues
{
    public const string RecordType = nameof(SeedingRecord);
    public const string Created = "SeedingRecord.Created";
    public const string Corrected = "SeedingRecord.Corrected";
    public const string Removed = "SeedingRecord.Removed";
    public const string CycleCompleted = "CultivationCycle.SeedingCompleted";

    public const string DateField = "Seeding date";
    public const string SeedLotField = "Seed lot";
    public const string LanesField = "Lanes";
    public const string QuantityField = "Quantity";
    public const string ReasonField = "Reason";

    public const int MaxReasonLength = 1000;

    public static string LotCode(PurchaseReceiptLine lot) => DocumentCodes.SeedLotCode(lot.Receipt.Code, lot.LineNumber);

    /// <summary>The audit values: codes and numbers rather than ids, as a reader of the log knows them. The lot comes with its receipt and the lanes with their numbers.</summary>
    public static object Snapshot(SeedingRecord record) => new
    {
        record.SeedingDate,
        SeedLot = LotCode(record.SeedLotLine),
        Lanes = record.Lanes.Select(l => l.Lane.LaneNumber).Order().ToList(),
        LanePercents = record.Lanes.OrderBy(l => l.Lane.LaneNumber).Select(l => $"{l.Lane.LaneNumber}: {l.SeededPercent:0.##}%").ToList(),
        record.Quantity,
        Photos = record.Photos.Count,
        record.OverPlanReason,
    };

    /// <summary>The record code is the code of the raft, as a session has none. The cycle comes with its raft.</summary>
    public static AuditEntry Entry(string action, SeedingRecord record) => new(action, RecordType)
    {
        RecordId = record.Id, RecordCode = record.Cycle.Raft.Code, ZoneId = record.Cycle.Raft.ZoneId,
    };
}
