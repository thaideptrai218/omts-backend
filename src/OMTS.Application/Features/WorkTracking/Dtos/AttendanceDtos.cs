using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkTracking.Dtos;

/// <summary>
/// One row of the attendance sheet: exactly one of <see cref="EmployeeId"/>, <see cref="WorkerId"/> or <see cref="TeamId"/>.
/// A team row applies to every In Use member; an individual row of a member overrides it.
/// </summary>
/// <param name="TeamOfDayId">Worker rows only: team of the day (default: the current team of the worker).</param>
/// <param name="Reason">Required when the row changes an attendance record already saved.</param>
/// <param name="RowVersion">
/// The version of the saved record the row was read from; required when the row changes a saved record (MSG01 when
/// missing), an older one answers MSG06. Not used by team rows, see <see cref="SaveAttendanceRequest.MemberRowVersions"/>.
/// </param>
public sealed record SaveAttendanceRow(
    Guid? EmployeeId,
    Guid? WorkerId,
    Guid? TeamId,
    AttendanceResult? Result,
    decimal? Hours = null,
    Guid? TeamOfDayId = null,
    string? Notes = null,
    string? Reason = null,
    string? RowVersion = null);

/// <param name="MemberRowVersions">
/// Team rows only: worker id → rowVersion of the member's saved record, for every member whose saved record the team
/// row would change (a missing version is MSG01, a stale one MSG06).
/// </param>
public sealed record SaveAttendanceRequest(
    IReadOnlyList<SaveAttendanceRow>? Rows,
    IReadOnlyDictionary<Guid, string>? MemberRowVersions = null);

/// <summary>Why one row of the sheet was refused; nothing of the sheet is saved then.</summary>
public sealed record AttendanceRowError(int Index, string? Person, string Code, string Message, string? Field);

/// <param name="Suggested">Not saved yet: proposed from an active daily-wage assignment of the zone on that day.</param>
/// <param name="Kind">Employee or Worker.</param>
public sealed record AttendanceSheetRow(
    Guid? RecordId,
    bool Suggested,
    string Kind,
    Guid PersonId,
    string Name,
    TeamRef? Team,
    AttendanceResult? Result,
    decimal? Hours,
    string? Notes,
    IReadOnlyList<string> EvidenceUrls,
    Guid? AssignmentId,
    string? RowVersion);

/// <param name="Locked">The payroll of the month is finalized: nothing can be changed.</param>
public sealed record AttendanceSheet(Guid ZoneId, DateOnly Date, bool Locked, IReadOnlyList<AttendanceSheetRow> Rows);
