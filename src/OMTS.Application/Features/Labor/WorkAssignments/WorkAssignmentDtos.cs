using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.WorkAssignments;

/// <param name="Date">Today or later (BR-LABOR-03).</param>
/// <param name="EmployeeIds">The employees to assign; one job type and one raft for all of them.</param>
/// <param name="MoveConfirmed">The answer Yes to MSG-LABOR-06: an employee assigned to another raft and not checked in yet is moved.</param>
public sealed record AssignWorkRequest(DateOnly? Date, Guid? JobTypeId, IReadOnlyList<Guid>? EmployeeIds, bool MoveConfirmed = false);

/// <param name="RaftId">The new raft, or left out to keep it.</param>
/// <param name="JobTypeId">The new job type, or left out to keep it.</param>
/// <param name="RowVersion">The version the screen was read from; an older one answers MSG06.</param>
public sealed record EditWorkAssignmentRequest(Guid? RaftId, Guid? JobTypeId, string? RowVersion);

/// <param name="JobTypeId">The new job type of the selected members.</param>
/// <param name="NewRaftId">Another raft when the crew moves, or left out to stay.</param>
/// <param name="EmployeeIds">The members who change job; those who keep their job are left out.</param>
public sealed record CrewJobChangeRequest(Guid? JobTypeId, Guid? NewRaftId, IReadOnlyList<Guid>? EmployeeIds);

public sealed record WorkAssignmentView(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    DateOnly Date,
    Guid RaftId,
    string RaftCode,
    Guid ZoneId,
    Guid JobTypeId,
    string JobTypeName,
    DateTimeOffset? StartTime,
    string RowVersion);

public sealed record WorkAssignmentsSaved(IReadOnlyList<WorkAssignmentView> Assignments, string Message, string MessageCode);

public sealed record WorkAssignmentSaved(WorkAssignmentView Assignment, string Message, string MessageCode);

/// <param name="Result">What happened to the member: <see cref="CrewChangeResults"/>.</param>
public sealed record CrewMemberChange(Guid EmployeeId, string EmployeeName, string Result, Guid? AssignmentId);

public sealed record CrewJobChanged(IReadOnlyList<CrewMemberChange> Members, string Message, string MessageCode);

public static class CrewChangeResults
{
    /// <summary>The member is checked in: a new job period starts now.</summary>
    public const string PeriodAdded = "JobPeriodAdded";

    /// <summary>The member is not checked in yet: the work assignment changed.</summary>
    public const string AssignmentChanged = "AssignmentChanged";

    /// <summary>The member already has this job type on this raft.</summary>
    public const string Unchanged = "Unchanged";
}

/// <summary>The crew of the work-day view: who works on a raft on a date (SCR-LABOR-02, step 1).</summary>
public sealed record WorkDayView(DateOnly Date, bool CanAssign, IReadOnlyList<WorkDayRaft> Rafts, IReadOnlyList<WorkDayStaff> Staff);

/// <param name="CanChangeJob">At least one member is checked in and not checked out: Change job for crew is offered (LABOR-02.1).</param>
public sealed record WorkDayRaft(Guid Id, string Code, Guid ZoneId, string ZoneCode, bool CanChangeJob, IReadOnlyList<WorkDayCrewMember> Crew);

/// <param name="JobTypeId">The job type now: that of the latest job period.</param>
/// <param name="AssignmentId">The work assigned before the check-in, the one Edit and Remove address.</param>
/// <param name="CanEdit">The date is today or later and the member is not checked in yet (BR-LABOR-03).</param>
/// <param name="Hours">The rounded hours per job type of a complete day (BR-LABOR-05); null while the day is incomplete.</param>
public sealed record WorkDayCrewMember(
    Guid EmployeeId,
    string FullName,
    Guid AssignmentId,
    Guid JobTypeId,
    string JobTypeName,
    DateTimeOffset? CheckInAt,
    DateTimeOffset? CheckOutAt,
    bool CanEdit,
    string RowVersion,
    IReadOnlyList<WorkDayPeriod> Periods,
    IReadOnlyList<WorkDayHours>? Hours);

public sealed record WorkDayPeriod(Guid AssignmentId, Guid RaftId, string RaftCode, Guid JobTypeId, string JobTypeName, DateTimeOffset? StartTime);

public sealed record WorkDayHours(Guid JobTypeId, string JobTypeName, int Minutes);

public sealed record WorkDayStaff(Guid EmployeeId, string FullName, DateTimeOffset? CheckInAt, DateTimeOffset? CheckOutAt);

public sealed record AssignableAssignment(Guid AssignmentId, Guid RaftId, string RaftCode, string JobTypeName);

/// <param name="InYourZones">The employee has a working zone among the zones of the caller; false for one found by the search.</param>
/// <param name="MatchesJobType">The profile has the job type asked for, so the employee comes first.</param>
/// <param name="Assignment">The work assigned on the date, if any.</param>
/// <param name="CheckedIn">Checked in on the date: assigned only with Change job for crew (MSG-LABOR-07).</param>
public sealed record AssignableEmployee(
    Guid Id, string FullName, string? PhoneNumber, EmployeeType? Type, bool InYourZones, bool MatchesJobType, bool CheckedIn, AssignableAssignment? Assignment);

/// <param name="Truncated">More employees match than are listed: narrow the search.</param>
public sealed record AssignableEmployees(DateOnly Date, IReadOnlyList<AssignableEmployee> Items, bool Truncated);
