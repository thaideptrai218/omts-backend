namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>An employee on a plan line: the assignment made for it, and whether the employee has checked in that day (then the assignment can no longer be taken off).</summary>
public sealed record HarvestPlanStaffingEmployee(Guid EmployeeId, string Code, string Name, Guid AssignmentId, bool CheckedIn);

/// <summary>
/// The crew of one plan line. <paramref name="PlannedDate"/> is the day of the plan (UTC+7), which is the day of every assignment. <paramref name="CanEdit"/>: the caller may choose the crew of this line now
/// (see <see cref="HarvestPlanStaffingRules.Refusal"/>), and the day has not passed.
/// </summary>
public sealed record HarvestPlanStaffingLine(
    Guid LineId,
    Guid RaftId,
    string RaftCode,
    Guid ZoneId,
    string ZoneCode,
    IReadOnlyList<int> LaneNumbers,
    DateOnly PlannedDate,
    IReadOnlyList<HarvestPlanStaffingEmployee> Employees,
    bool CanEdit);

/// <summary>The crew of the lines of a plan the caller sees. <paramref name="CanEdit"/>: the caller may choose the crew of at least one of them. <paramref name="RowVersion"/> is the plan's. <paramref name="Roster"/> is the people chosen for the whole plan with their answers and the people needed (see <see cref="HarvestPlanRoster"/>).</summary>
public sealed record HarvestPlanStaffing(Guid PlanId, string PlanCode, string RowVersion, IReadOnlyList<HarvestPlanStaffingLine> Lines, bool CanEdit, HarvestPlanRoster? Roster = null);

/// <summary>
/// The whole crew wanted on a line (PUT): employees not listed are taken off, new ones are assigned. <paramref name="MoveExisting"/> moves an employee already on another raft that day (the first answer is a
/// 409 MSG-LABOR-06 naming them); <paramref name="RowVersion"/> is the plan's, checked when sent.
/// </summary>
public sealed record SaveHarvestPlanLineStaffingRequest(IReadOnlyList<Guid>? EmployeeIds, bool MoveExisting = false, string? RowVersion = null);
