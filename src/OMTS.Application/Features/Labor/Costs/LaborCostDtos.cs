using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Costs;

/// <summary>The period of the cost by raft: both dates are included; at most one year. <paramref name="ZoneIds"/> (repeat the parameter, at most 200) and <paramref name="ZoneId"/> together keep the cost of those zones.</summary>
public sealed record LaborCostQuery(DateOnly From, DateOnly To, Guid? ZoneId, Guid[]? ZoneIds = null);

/// <summary>The hours, shifts counted and cost of one job type inside a raft, a cycle or an employee's share of one.</summary>
public sealed record JobLaborCost(Guid JobTypeId, string JobName, decimal Hours, decimal Amount, decimal Shifts = 0);

/// <summary>The hours, shifts and cost of the work of employees of one type (Seasonal worker or Staff), by their type on the day worked.</summary>
public sealed record TypeLaborCost(EmployeeType EmployeeType, decimal Hours, decimal Shifts, decimal Amount);

/// <summary>The hours, shifts and cost of the work paid one way: <c>Hourly</c> (hours times rate), <c>Shift</c> (shifts times rate) or <c>FieldShift</c> (field shifts of a fixed-salary employee).</summary>
public sealed record KindLaborCost(PayLineKind Kind, decimal Hours, decimal Shifts, decimal Amount);

/// <summary>
/// The labour cost of one raft in the period; <c>RaftId</c> is null for the work done at zone level (nursery), which is the cost of the zone itself. <c>Hours</c> are the paid hours (break
/// deducted) and <c>ByJob</c> the split by job type; <c>ByType</c> and <c>ByKind</c> split the same cost by employee type and by the way it was paid. All add up to <c>Amount</c>.
/// </summary>
public sealed record RaftLaborCost(
    Guid? RaftId, string? RaftCode, Guid ZoneId, string ZoneName, decimal Amount, decimal Hours = 0, IReadOnlyList<JobLaborCost>? ByJob = null,
    decimal Shifts = 0, IReadOnlyList<TypeLaborCost>? ByType = null, IReadOnlyList<KindLaborCost>? ByKind = null);

public sealed record LaborCostByRaft(
    DateOnly From, DateOnly To, decimal Total, IReadOnlyList<RaftLaborCost> Items, decimal TotalHours = 0, decimal TotalShifts = 0,
    IReadOnlyList<TypeLaborCost>? ByType = null, IReadOnlyList<KindLaborCost>? ByKind = null);

public sealed record DayLaborCost(DateOnly Date, decimal Amount);

/// <summary>One employee's share of a cycle's labour cost (BR-LABOR-07): hours, pay and the job types.</summary>
public sealed record EmployeeLaborCost(
    Guid EmployeeId, string? EmployeeCode, string EmployeeName, decimal Hours, decimal Amount, IReadOnlyList<JobLaborCost> ByJob, decimal Shifts = 0);

/// <summary>The labour cost of one cultivation cycle: the raft's cost on the days the cycle was Open, the close day included (BR-LABOR-07, BR-CYCLE-05).</summary>
public sealed record CycleLaborCost(
    Guid CycleId, Guid RaftId, string RaftCode, DateOnly StartDate, DateOnly? CloseDate, decimal Amount, IReadOnlyList<DayLaborCost> Days,
    decimal Hours = 0, IReadOnlyList<JobLaborCost>? ByJob = null, IReadOnlyList<EmployeeLaborCost>? Employees = null,
    decimal Shifts = 0, IReadOnlyList<TypeLaborCost>? ByType = null, IReadOnlyList<KindLaborCost>? ByKind = null);
