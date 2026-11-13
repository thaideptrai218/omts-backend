using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Employees;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// The line length of one lane of a raft (decision of 12/11/2026). <paramref name="LineLengthM"/> null puts the lane back to the length of the raft.
/// </summary>
public sealed record LaneLengthInput(int? LaneNumber, decimal? LineLengthM);

/// <summary>
/// FARM-04 create/edit body. The map pin (<see cref="Latitude"/>, <see cref="Longitude"/>) is optional, both or none;
/// <see cref="LaneCount"/> is the number of lanes In Use; <see cref="RowVersion"/> is required on edit only.
/// <see cref="LengthM"/> and <see cref="WidthM"/> (metres, both together) are the numeric geometry of 12/11/2026: when sent, <see cref="Size"/> is ignored and written by the server as
/// "{length} m x {width} m"; a request with neither is the older form and sends <see cref="Size"/> alone (read back into the numbers when it has that form). The area is never sent.
/// <see cref="LaneLengths"/> sets the line length of the lanes it names and leaves the others as they are; a null length puts a lane back to the length of the raft.
/// </summary>
public sealed record SaveRaftRequest(
    string? Code,
    decimal? Latitude,
    decimal? Longitude,
    string? Location,
    string? Size,
    string? ConditionNotes,
    int? LaneCount,
    string? RowVersion = null,
    decimal? LengthM = null,
    decimal? WidthM = null,
    IReadOnlyList<LaneLengthInput>? LaneLengths = null);

/// <summary>The proposed code of a new raft of the zone (GBR-03).</summary>
/// <param name="Code">e.g. KV01-B03: the highest number in use in the zone (discontinued rafts included) plus one.</param>
public sealed record RaftCodeProposal(string Code);

/// <summary>
/// A lane with its status In Use or Removed; a Removed lane keeps its number and records (BR-FARM-04).
/// </summary>
/// <param name="LineLengthM">The line length in effect: the own one of the lane, else the length of the raft; null when neither is known.</param>
/// <param name="LineLengthOverridden">The lane has a line length of its own.</param>
/// <param name="IsDamaged">The lane is reported damaged and not yet repaired: no seeding and no harvest on it.</param>
/// <param name="IsOutOfService">The lane was put out of service: it is left out of every list of lanes to use and its number is never reused.</param>
/// <param name="OutOfServiceOn">The day it was put out of service.</param>
public sealed record LaneView(
    Guid Id,
    int LaneNumber,
    int StatusId,
    string Status,
    decimal? LineLengthM = null,
    bool LineLengthOverridden = false,
    bool IsDamaged = false,
    bool IsOutOfService = false,
    DateOnly? OutOfServiceOn = null);

/// <param name="Url">Signed read link; never store it.</param>
public sealed record RaftPhotoView(Guid Id, int SortOrder, string Url);

/// <param name="LaneCount">The number of lanes In Use.</param>
/// <param name="AreaM2">Length times width, worked out; null when a side is unknown.</param>
/// <param name="DamagedLaneCount">Lanes In Use that are damaged now.</param>
/// <param name="OutOfServiceLaneCount">Lanes In Use that were put out of service.</param>
public sealed record RaftListItem(
    Guid Id, Guid ZoneId, string Code, decimal? Latitude, decimal? Longitude, string? Size, int LaneCount, int StatusId, string Status,
    decimal? LengthM = null, decimal? WidthM = null, decimal? AreaM2 = null, int DamagedLaneCount = 0, int OutOfServiceLaneCount = 0);

/// <summary>A raft in the filters of purchase receipts and material cost: names it, and nothing else of FARM-04.</summary>
public sealed record RaftLookupItem(Guid Id, string Code, string ZoneCode, Guid ZoneId);

/// <param name="Photos">At most three, in slot order.</param>
/// <param name="LaneCount">The number of lanes In Use.</param>
/// <param name="Lanes">Every lane of the raft in number order, the Removed ones included.</param>
/// <param name="MaterialCost">VND of every material used on the raft at the latest purchase price of each item (BR-FARM-05); 0 when none was used.</param>
/// <param name="LengthM">Metres along the lines; null for a raft whose old size text could not be read.</param>
/// <param name="WidthM">Metres across the lanes; null as <paramref name="LengthM"/>.</param>
/// <param name="AreaM2">Length times width rounded to 2 decimals; null when a side is unknown.</param>
/// <param name="TotalLineLengthM">Two lines per lane In Use: the metres of line the lanes need; null when a length is unknown.</param>
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
    string RowVersion,
    decimal? LengthM = null,
    decimal? WidthM = null,
    decimal? AreaM2 = null,
    decimal? TotalLineLengthM = null,
    int DamagedLaneCount = 0,
    int OutOfServiceLaneCount = 0);

/// <summary>Answer of a raft create, edit or photo change (FARM-04.0 step 6), in the shape of the zone saves.</summary>
/// <param name="Message">MSG-GEN-05, e.g. "Raft saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
/// <param name="Warnings">Non-blocking remarks: the pin more than 5 km from its zone, lanes narrower than 1 m across, lanes of unequal length; empty when there is none.</param>
public sealed record RaftSaved(RaftView Raft, string Message, string MessageCode, IReadOnlyList<FarmingWarning>? Warnings = null);

/// <summary>FARM-04.1 every lane of a raft today with its status In Use or Removed and its state of the day (BR-FARM-03).</summary>
public sealed record RaftLanesToday(Guid RaftId, string RaftCode, DateOnly Date, IReadOnlyList<LaneDayView> Lanes);
