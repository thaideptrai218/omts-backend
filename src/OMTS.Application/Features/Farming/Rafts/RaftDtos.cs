using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>One lane of the layout: lanes are numbered 1…N; positions are metres from marker A.</summary>
public sealed record LaneInput(int LaneNumber, decimal? Length, string? MarkerA, string? MarkerB);

/// <summary>
/// FARM-04 create/edit body. <see cref="LayoutDrawing"/> is the web layout editor's JSON (≤ 50 KB), stored
/// as-is; <see cref="RowVersion"/> is required on edit only.
/// </summary>
public sealed record SaveRaftRequest(
    string? Code,
    decimal? Latitude,
    decimal? Longitude,
    string? Location,
    string? Size,
    string? ConditionNotes,
    IReadOnlyList<LaneInput>? Lanes,
    JsonElement? LayoutDrawing = null,
    string? RowVersion = null);

public sealed record LaneView(Guid Id, int LaneNumber, decimal? Length, string? MarkerA, string? MarkerB);

public sealed record LayoutVersionView(int VersionNumber, int LaneCount, DateOnly ValidFrom, DateOnly? ValidTo);

public sealed record RaftListItem(
    Guid Id, Guid ZoneId, string Code, decimal? Latitude, decimal? Longitude, string? Size, int LaneCount, int StatusId, string Status);

/// <param name="MaterialCost">Total of the purchase receipts linked to the raft (INP-03); null until purchase receipts exist, never 0 for unknown.</param>
public sealed record RaftView(
    Guid Id,
    ZoneRef Zone,
    string Code,
    decimal? Latitude,
    decimal? Longitude,
    string? Location,
    string? Size,
    string? ConditionNotes,
    string? PhotoUrl,
    int StatusId,
    string Status,
    int LayoutVersion,
    IReadOnlyList<LaneView> Lanes,
    JsonElement? LayoutDrawing,
    IReadOnlyList<LayoutVersionView> LayoutVersions,
    decimal? MaterialCost,
    string RowVersion);

/// <summary>A recorded segment behind a portion of the raft map; no kg is inferred from it.</summary>
public sealed record SegmentSourceView(
    Guid Id,
    LaneSegmentType Type,
    decimal From,
    decimal To,
    DateOnly Date,
    string? RecordedBy,
    string? ConfirmedBy,
    Guid? SeedingEventId,
    string? SeedLotCode,
    Guid? HarvestEventId,
    Guid? LotId,
    string? WriteOffReason);

public sealed record PortionView(decimal From, decimal To, PortionCondition Condition, IReadOnlyList<SegmentSourceView> Segments);

public sealed record LaneConditionView(
    int LaneNumber, decimal Length, LaneSummary Summary, IReadOnlyList<PortionView> Portions, IReadOnlyList<SegmentSourceView> PlannedHarvests);

/// <param name="CycleId">The cycle the view is built from; null when the raft had no cycle on that date.</param>
public sealed record RaftConditionView(Guid RaftId, string RaftCode, DateOnly Date, Guid? CycleId, int LayoutVersion, IReadOnlyList<LaneConditionView> Lanes);
