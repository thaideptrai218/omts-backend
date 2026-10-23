using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>People who can be picked for work or attendance in a zone (LAB-03/LAB-04 pickers).</summary>
public sealed record PickablePeople(IReadOnlyList<Employee> Employees, IReadOnlyList<SeasonalWorker> Workers, IReadOnlyList<Contractor> Teams);

/// <summary>Seasonal workers and teams (LAB-02); cooperative-wide, not tied to a zone.</summary>
public interface IWorkforceRepository
{
    /// <summary>Workers by name, phone or citizen ID (contains), team and status, ordered by name, with their team.</summary>
    Task<(IReadOnlyList<SeasonalWorker> Items, int Total)> SearchWorkersAsync(
        string? search, Guid? teamId, int? statusId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The worker with team and linked employee, tracked.</summary>
    Task<SeasonalWorker?> GetWorkerAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Workers with their team, tracked.</summary>
    Task<IReadOnlyList<SeasonalWorker>> GetWorkersAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Another worker with the same full name + phone (case-insensitive), or the same citizen ID.</summary>
    Task<SeasonalWorker?> FindSameWorkerAsync(
        string fullName, string? phoneNumber, string? citizenId, Guid? exceptWorkerId, CancellationToken cancellationToken);

    /// <summary>Employees with their linked account and working zones, tracked.</summary>
    Task<IReadOnlyList<Employee>> GetEmployeesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Employed employees with this full name (case-insensitive) and phone.</summary>
    Task<IReadOnlyList<Employee>> FindEmployedSamePersonAsync(string fullName, string phoneNumber, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Contractor> Items, int Total)> SearchTeamsAsync(
        string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The team with leader and members, tracked.</summary>
    Task<Contractor?> GetTeamAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Teams with their leader, tracked.</summary>
    Task<IReadOnlyList<Contractor>> GetTeamsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    Task<Contractor?> FindTeamByNameAsync(string name, Guid? exceptTeamId, CancellationToken cancellationToken);

    /// <summary>Teams the worker leads, tracked.</summary>
    Task<IReadOnlyList<Contractor>> GetTeamsLedByAsync(Guid workerId, CancellationToken cancellationToken);

    /// <summary>In Use members of the teams, with their team.</summary>
    Task<IReadOnlyList<SeasonalWorker>> GetInUseMembersAsync(IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken);

    /// <summary>Periods ("YYYY-MM") of the team payslips that are Draft, or Approved with an amount still to pay.</summary>
    Task<IReadOnlyList<string>> GetUnsettledPayslipPeriodsAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>Employed employees working in the zone, In Use workers and teams matching the name (picker).</summary>
    Task<PickablePeople> ListPickableAsync(Guid zoneId, string? search, CancellationToken cancellationToken);

    void AddWorker(SeasonalWorker worker);

    void AddTeam(Contractor team);
}
