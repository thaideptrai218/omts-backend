using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Pay rate versions (LAB-06).</summary>
public interface IPayRateRepository
{
    /// <summary>Every version of one person (exactly one id set), newest first, tracked.</summary>
    Task<IReadOnlyList<PayRuleVersion>> ListForPersonAsync(Guid? employeeId, Guid? workerId, CancellationToken cancellationToken);

    /// <summary>The version with its person, tracked.</summary>
    Task<PayRuleVersion?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Versions overlapping [from, to] (payroll), not tracked.</summary>
    Task<IReadOnlyList<PayRuleVersion>> ListOverlappingAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>Periods ("YYYY-MM") of Approved or Fully Paid payslips with a line calculated from each given version.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetFinalizedPeriodsUsingAsync(
        IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken);

    /// <summary>Employed employees and In Use workers whose name matches (pay-rate people list), ordered by name.</summary>
    Task<(IReadOnlyList<Employee> Employees, IReadOnlyList<SeasonalWorker> Workers)> ListPayablePeopleAsync(
        string? search, CancellationToken cancellationToken);

    void Add(PayRuleVersion version);
}
