using System.Text.Json.Serialization;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Work;

// ---- LABOR-02: work assignment ----

/// <param name="RaftId">An In Use raft of the caller's zones.</param>
/// <param name="JobTypeId">A job type In Use.</param>
/// <param name="Date">Today or later (BR-LABOR-03).</param>
/// <param name="EmployeeIds">One to 100 Employed employees, no repeats.</param>
/// <param name="MoveExisting">Yes to MSG-LABOR-06: an employee already assigned to another raft that day, not checked in, is moved.</param>
public sealed record AssignWorkRequest(Guid? RaftId, Guid? JobTypeId, DateOnly? Date, IReadOnlyList<Guid>? EmployeeIds, bool MoveExisting = false);

/// <summary>Edit of an assignment (LABOR-02.2): its raft and job type, before the employee is checked in.</summary>
public sealed record EditAssignmentRequest(Guid? RaftId, Guid? JobTypeId);

/// <summary>Change job for crew (LABOR-02.1): the new job type and raft of the selected members, today.</summary>
public sealed record CrewJobRequest(Guid? RaftId, Guid? JobTypeId, IReadOnlyList<Guid>? EmployeeIds);

/// <summary>
/// One employee of a day on a raft: the current job period (the latest of the day), with the check-in and the check-out. <see cref="Id"/>
/// is the first job period of the day, which stays the id of the row whatever job changes follow.
/// </summary>
/// <remarks>
/// <c>complete</c>: the attendance record has both times; a check-out without a check-in is incomplete (BR-LABOR-06). <c>onLeave</c>: the
/// employee has approved leave on this day (BR-LABOR-08). <c>message</c> and <c>messageCode</c> are set only on the answer of a check-in or
/// check-out: MSG-GEN-05, or MSG-LABOR-12 when the check-out has no check-in.
/// </remarks>
public sealed record AssignmentView(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid ZoneId,
    Guid? RaftId,
    string? RaftCode,
    Guid? JobTypeId,
    string Job,
    DateOnly Date,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CheckedOutAt)
{
    public string? EmployeeCode { get; init; }
    public bool Complete { get; init; }
    public bool OnLeave { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageCode { get; init; }
}

public sealed record WorkJobOption(Guid Id, string Name);

/// <param name="OnLeave">Approved leave covers the day (BR-LABOR-08).</param>
/// <remarks><c>jobTypeIds</c> are the job types of the profile (the client lists those with the chosen job type first, LABOR-02 step 5); <c>raftCode</c> is the raft of the assignment of the day, if any.</remarks>
public sealed record WorkEmployeeOption(Guid Id, string Name, bool OnLeave)
{
    public string? Code { get; init; }
    public string? PhoneNumber { get; init; }
    public EmployeeType Type { get; init; }
    public IReadOnlyList<Guid> JobTypeIds { get; init; } = [];
    public string? RaftCode { get; init; }
    public bool CheckedIn { get; init; }
}

/// <summary>What the Work and attendance screen picks from (SCR-LABOR-02): the job types In Use and the Employed employees of the caller's zones.</summary>
public sealed record WorkLookups(IReadOnlyList<WorkJobOption> Jobs, IReadOnlyList<WorkEmployeeOption> Employees);

// ---- LABOR-04: attendance ----

/// <param name="QrCode">The text scanned from the attendance QR code of the employee; null when the Zone Manager marks it by hand.</param>
public sealed record RecordAttendanceRequest(string? QrCode);

/// <param name="StartTime">Business time of today (UTC+7) the crew started, <c>HH:mm</c> or <c>HH:mm:ss</c>, not later than now.</param>
public sealed record CrewStartRequest(Guid? RaftId, string? StartTime, string? Reason, IReadOnlyList<Guid>? EmployeeIds);

/// <summary>A Staff employee on the staff list (LABOR-04.7): no raft, no job period.</summary>
public sealed record StaffAttendanceView(Guid EmployeeId, string EmployeeName, string? EmployeeCode, DateOnly Date, DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt, bool Complete, bool OnLeave)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageCode { get; init; }
}

/// <summary>One job period of a day as entered in a correction (LABOR-04.4), and as shown.</summary>
/// <param name="StartsAt">Empty for the first period of the day, which starts at the check-in.</param>
public sealed record DayPeriodInput(Guid? JobTypeId, Guid? RaftId, DateTimeOffset? StartsAt);

/// <param name="CheckInAt">Null leaves the check-in missing (an incomplete day).</param>
/// <param name="Periods">The job periods of the day after the correction, replacing the earlier ones: of a seasonal worker, each with a job type and a raft; of a Staff employee, none.</param>
/// <param name="Reason">Mandatory (LABOR-04.4.E2).</param>
/// <param name="RowVersion">The version of the attendance record the screen was read from; empty for a day with no record.</param>
public sealed record CorrectAttendanceRequest(
    DateTimeOffset? CheckInAt, DateTimeOffset? CheckOutAt, IReadOnlyList<DayPeriodInput>? Periods, string? Reason, string? RowVersion);

public sealed record DayPeriodView(Guid Id, Guid JobTypeId, string Job, Guid RaftId, string RaftCode, DateTimeOffset? StartsAt);

public sealed record DayJobHoursView(Guid JobTypeId, string Job, decimal Hours);

/// <summary>The attendance and job periods of one employee on one day, with the hours worked out when it is complete (SCR-LABOR-02 Correct attendance).</summary>
public sealed record AttendanceDayView(
    Guid EmployeeId,
    string EmployeeName,
    EmployeeType Type,
    DateOnly Date,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CheckedOutAt,
    AttendanceMethod? CheckInMethod,
    AttendanceMethod? CheckOutMethod,
    bool Complete,
    IReadOnlyList<DayPeriodView> Periods,
    IReadOnlyList<DayJobHoursView> Jobs,
    string? RowVersion);

/// <param name="Message">MSG-GEN-05.</param>
public sealed record AttendanceCorrected(AttendanceDayView Day, string Message, string MessageCode);

// ---- LABOR-10, LABOR-05: employee requests ----

/// <summary>
/// An employee request as the screens show it. <see cref="Approved"/> is true once approved, false once rejected, and null while Awaiting
/// review or after a cancellation; <see cref="Response"/> is the Owner's note. For leave, <see cref="From"/> to <see cref="To"/> are the days;
/// for a missed check-in or check-out both are the day and <see cref="RequestedTime"/> is the time to record.
/// </summary>
/// <remarks><c>message</c> and <c>messageCode</c> are set only on the answer of a submit: MSG-LABOR-22.</remarks>
public sealed record EmployeeRequestView(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    EmployeeRequestType Type,
    DateOnly From,
    DateOnly To,
    TimeOnly? RequestedTime,
    string Reason,
    bool? Approved,
    string? Response,
    string RowVersion,
    DateTimeOffset? CancelledAt,
    int StatusId,
    string Status,
    DateTimeOffset SentAt,
    DateTimeOffset? ReviewedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageCode { get; init; }
}

/// <param name="Type">Leave (the default) or MissedCheckIn / MissedCheckOut.</param>
/// <param name="From">First day of leave; also the day of a missed time when <paramref name="Day"/> is not sent.</param>
/// <param name="To">Last day of leave.</param>
/// <param name="Day">The day of a missed check-in or check-out, today or earlier.</param>
/// <param name="RequestedTime">The time to record for a missed check-in or check-out (business time, UTC+7), <c>HH:mm</c> or <c>HH:mm:ss</c>.</param>
/// <param name="ClientOperationId">One per submit: a retry of the same one returns the request already saved.</param>
public sealed record SubmitEmployeeRequest(
    EmployeeRequestType? Type, DateOnly? From, DateOnly? To, DateOnly? Day, string? RequestedTime, string? Reason, Guid? ClientOperationId);

/// <param name="Approved">True approves; false rejects (the note is then mandatory).</param>
/// <param name="Response">The note of the Owner.</param>
/// <param name="JobTypeId">For a missed check-in of a seasonal worker with no work that day: the job type of that day (LABOR-05.3).</param>
/// <param name="RaftId">The raft of that day.</param>
public sealed record ReviewEmployeeRequest(bool? Approved, string? Response, string? RowVersion, Guid? JobTypeId = null, Guid? RaftId = null);

// ---- my work (LABOR-08, LABOR-10) ----

/// <summary>The hours of one job type in a job period, shown to the employee.</summary>
public sealed record MyPeriodView(Guid JobTypeId, string Job, string? Raft, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int Minutes);

public sealed record MyJobHoursView(Guid JobTypeId, string Job, decimal Hours);

/// <param name="Periods">Empty while the day is incomplete (BR-LABOR-06) and for a Staff employee, who has days worked and no hours.</param>
/// <param name="Complete">Both times are recorded: the day counts (a Staff employee: one day worked).</param>
public sealed record MyAttendanceDayView(
    DateOnly Date,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CheckedOutAt,
    IReadOnlyList<MyPeriodView> Periods,
    IReadOnlyList<MyJobHoursView> Jobs,
    bool Complete);

/// <summary>The own work of the employee linked to the caller's account (LABOR-08); every field is empty when the account has no employee profile.</summary>
public sealed record MyWork(
    Guid? EmployeeId,
    string? Name,
    IReadOnlyList<AssignmentView> Assignments,
    IReadOnlyList<MyAttendanceDayView> Attendance,
    IReadOnlyList<EmployeeRequestView> Requests);

/// <summary>The attendance QR code the employee app shows (LABOR-07).</summary>
public sealed record MyAttendanceQr(string Code, DateTimeOffset ExpiresAt, int SecondsValid);
