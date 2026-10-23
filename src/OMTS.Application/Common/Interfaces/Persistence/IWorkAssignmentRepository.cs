using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Assignment list filters (LAB-03): date range [From, To], assignee, payment type and state.</summary>
/// <param name="State">"active", "cancelled" or "completed"; null for all.</param>
public sealed record WorkAssignmentFilter(
    DateOnly From, DateOnly To, Guid? EmployeeId, Guid? WorkerId, Guid? TeamId, WorkPaymentType? PaymentType, string? State);

/// <summary>Work assignments (LAB-03, LAB-05) and the rafts and cycles they may point to.</summary>
public interface IWorkAssignmentRepository
{
    /// <summary>The assignment with zone, assignee (employee with account, worker, team), tracked.</summary>
    Task<WorkAssignment?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<WorkAssignment> Items, int Total)> SearchAsync(
        Guid zoneId, WorkAssignmentFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Not-cancelled assignments on the date of any of the employees, workers or teams (any zone), with assignees.</summary>
    Task<IReadOnlyList<WorkAssignment>> ListActiveOnDateAsync(
        DateOnly date, IReadOnlyCollection<Guid> employeeIds, IReadOnlyCollection<Guid> workerIds, IReadOnlyCollection<Guid> teamIds,
        Guid? exceptAssignmentId, CancellationToken cancellationToken);

    /// <summary>Not-cancelled daily-wage assignments of the zone on the date, with assignees (attendance suggestions).</summary>
    Task<IReadOnlyList<WorkAssignment>> ListActiveDailyWageAsync(Guid zoneId, DateOnly date, CancellationToken cancellationToken);

    /// <summary>Confirmed, not-cancelled piecework completed in [from, to] (payroll), not tracked.</summary>
    Task<IReadOnlyList<WorkAssignment>> ListConfirmedPieceworkAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<Raft?> GetRaftAsync(Guid id, CancellationToken cancellationToken);

    Task<CultivationCycle?> GetCycleAsync(Guid id, CancellationToken cancellationToken);

    Task<FarmingZone?> GetZoneAsync(Guid id, CancellationToken cancellationToken);

    void Add(WorkAssignment assignment);
}
