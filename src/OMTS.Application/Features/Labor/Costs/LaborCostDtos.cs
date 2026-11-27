namespace OMTS.Application.Features.Labor.Costs;

/// <summary>The period of the cost by raft: both dates are included; at most one year. <paramref name="ZoneIds"/> (repeat the parameter, at most 200) and <paramref name="ZoneId"/> together keep the cost of those zones.</summary>
public sealed record LaborCostQuery(DateOnly From, DateOnly To, Guid? ZoneId, Guid[]? ZoneIds = null);

/// <summary>The hours and cost of one job type inside a raft, a cycle or an employee's share of one.</summary>
public sealed record JobLaborCost(Guid JobTypeId, string JobName, decimal Hours, decimal Amount);

/// <summary>
/// The labour cost of one raft in the period; <c>RaftId</c> is null for the work done at zone level (nursery), which is the cost of the zone itself. <c>Hours</c> are the paid hours (break
/// deducted) and <c>ByJob</c> the split by job type; both add up to <c>Amount</c>.
/// </summary>
public sealed record RaftLaborCost(
    Guid? RaftId, string? RaftCode, Guid ZoneId, string ZoneName, decimal Amount, decimal Hours = 0, IReadOnlyList<JobLaborCost>? ByJob = null);

public sealed record LaborCostByRaft(DateOnly From, DateOnly To, decimal Total, IReadOnlyList<RaftLaborCost> Items, decimal TotalHours = 0);

public sealed record DayLaborCost(DateOnly Date, decimal Amount);

/// <summary>One employee's share of a cycle's labour cost (BR-LABOR-07): hours, pay and the job types.</summary>
public sealed record EmployeeLaborCost(Guid EmployeeId, string? EmployeeCode, string EmployeeName, decimal Hours, decimal Amount, IReadOnlyList<JobLaborCost> ByJob);

/// <summary>The labour cost of one cultivation cycle: the raft's cost on the days the cycle was Open, the close day included (BR-LABOR-07, BR-CYCLE-05).</summary>
public sealed record CycleLaborCost(
    Guid CycleId, Guid RaftId, string RaftCode, DateOnly StartDate, DateOnly? CloseDate, decimal Amount, IReadOnlyList<DayLaborCost> Days,
    decimal Hours = 0, IReadOnlyList<JobLaborCost>? ByJob = null, IReadOnlyList<EmployeeLaborCost>? Employees = null);