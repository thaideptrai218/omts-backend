using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>The audit rows, field names and labels of the nursery (ươm giống, decision of 12/11/2026).</summary>
public static class NurseryValues
{
    public const string RecordType = nameof(NurseryBatch);
    public const string Created = "NurseryBatch.Created";
    public const string Edited = "NurseryBatch.Edited";
    public const string Cancelled = "NurseryBatch.Cancelled";
    public const string Completed = "NurseryBatch.Completed";
    public const string SurvivorsCorrected = "NurseryBatch.SurvivorsCorrected";
    public const string AllocationsChanged = "NurseryBatch.AllocationsChanged";

    public const string ZoneField = "Zone";
    public const string SeedLotField = "Seed lot";
    public const string QuantityField = "Quantity";
    public const string StartDateField = "Start date";
    public const string NoteField = "Note";
    public const string SurvivorsField = "Survivors";
    public const string CompletedOnField = "Completed on";
    public const string AllocationsField = "Allocations";
    public const string ReasonField = "Reason";
    public const string TargetYieldField = "Target yield";

    public const int MaxReasonLength = 1000;
    public const int MaxAllocations = 100;

    /// <summary>The audit values of a batch: codes and numbers rather than ids, as a reader of the history knows them.</summary>
    public static object Snapshot(NurseryBatch batch) => new
    {
        Status = StatusLabel(batch.StatusId),
        batch.StartDate,
        SeedLot = batch.SeedLotLine is { } lot ? Seeding.SeedingValues.LotCode(lot) : null,
        Quantity = batch.QuantityIn,
        batch.Note,
        batch.Survivors,
        batch.CompletedOn,
        Allocations = batch.Allocations.OrderBy(a => a.Raft.Code, StringComparer.Ordinal).Select(a => $"{a.Raft.Code}: {a.Quantity:0.###}").ToList(),
    };

    public static AuditEntry Entry(string action, NurseryBatch batch) => new(action, RecordType) { RecordId = batch.Id, RecordCode = batch.Code, ZoneId = batch.ZoneId };

    /// <summary>The label of a status for the screens: Đang ươm, Đã hoàn tất, Đã hủy.</summary>
    public static string StatusLabel(int statusId) => statusId switch
    {
        StatusIds.NurseryBatch.Nursing => "Đang ươm",
        StatusIds.NurseryBatch.Completed => "Đã hoàn tất",
        StatusIds.NurseryBatch.Cancelled => "Đã hủy",
        _ => string.Empty,
    };
}
