using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Employees;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// FARM-04 create/edit body. The map pin (<see cref="Latitude"/>, <see cref="Longitude"/>) is optional, both or none;
/// <see cref="LaneCount"/> is the number of lanes In Use; <see cref="RowVersion"/> is required on edit only.
/// </summary>
public sealed record SaveRaftRequest(
    string? Code,
    decimal? Latitude,
    decimal? Longitude,
    string? Location,
    string? Size,
    string? ConditionNotes,
    int? LaneCount,
    string? RowVersion = null);

/// <summary>The proposed code of a new raft of the zone (GBR-03).</summary>
/// <param name="Code">e.g. KV01-B03: the highest number in use in the zone (discontinued rafts included) plus one.</param>
public sealed record RaftCodeProposal(string Code);

/// <summary>A lane with its status In Use or Removed; a Removed lane keeps its number and records (BR-FARM-04).</summary>
public sealed record LaneView(Guid Id, int LaneNumber, int StatusId, string Status);

/// <param name="Url">Signed read link; never store it.</param>
public sealed record RaftPhotoView(Guid Id, int SortOrder, string Url);

/// <param name="LaneCount">The number of lanes In Use.</param>
public sealed record RaftListItem(
    Guid Id, Guid ZoneId, string Code, decimal? Latitude, decimal? Longitude, string? Size, int LaneCount, int StatusId, string Status);

/// <summary>A raft in the filters of purchase receipts and material cost: names it, and nothing else of FARM-04.</summary>
public sealed record RaftLookupItem(Guid Id, string Code, string ZoneCode);

/// <param name="Photos">At most three, in slot order.</param>
/// <param name="LaneCount">The number of lanes In Use.</param>
/// <param name="Lanes">Every lane of the raft in number order, the Removed ones included.</param>
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
    IReadOnlyList<RaftPhotoView> Photos,
    int StatusId,
    string Status,
    int LaneCount,
    IReadOnlyList<LaneView> Lanes,
    decimal? MaterialCost,
    string RowVersion);

/// <summary>Answer of a raft create, edit or photo change (FARM-04.0 step 6), in the shape of the zone saves.</summary>
/// <param name="Message">MSG-GEN-05, e.g. "Raft saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record RaftSaved(RaftView Raft, string Message, string MessageCode);

/// <summary>FARM-04.1 every lane of a raft today with its status In Use or Removed and its state of the day (BR-FARM-03).</summary>
public sealed record RaftLanesToday(Guid RaftId, string RaftCode, DateOnly Date, IReadOnlyList<LaneDayView> Lanes);
