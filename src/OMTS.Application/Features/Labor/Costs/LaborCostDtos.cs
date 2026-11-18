namespace OMTS.Application.Features.Labor.Costs;

/// <summary>The period of the cost by raft: both dates are included; at most one year.</summary>
public sealed record LaborCostQuery(DateOnly From, DateOnly To, Guid? ZoneId);

/// <summary>The labour cost of one raft in the period; <c>RaftId</c> is null for the work done at zone level (nursery), which is the cost of the zone itself.</summary>
public sealed record RaftLaborCost(Guid? RaftId, string? RaftCode, Guid ZoneId, string ZoneName, decimal Amount);

public sealed record LaborCostByRaft(DateOnly From, DateOnly To, decimal Total, IReadOnlyList<RaftLaborCost> Items);

public sealed record DayLaborCost(DateOnly Date, decimal Amount);

/// <summary>The labour cost of one cultivation cycle: the raft's cost on the days the cycle was Open, the close day included (BR-LABOR-07, BR-CYCLE-05).</summary>
public sealed record CycleLaborCost(
    Guid CycleId, Guid RaftId, string RaftCode, DateOnly StartDate, DateOnly? CloseDate, decimal Amount, IReadOnlyList<DayLaborCost> Days);
