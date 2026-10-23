using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Payslip list filter (LAB-09): statuses and optional payroll period ("YYYY-MM").</summary>
public sealed record PayslipFilter(IReadOnlyCollection<int> StatusIds, string? Period);

/// <summary>
/// Payrolls and their payslips and lines (LAB-07, LAB-08). Loaded payslips always come with their lines,
/// payments and payee (employee, worker, or team with its leader).
/// </summary>
public interface IPayrollRepository
{
    /// <summary>The payroll of the month with its payslips, tracked.</summary>
    Task<Payroll?> GetAsync(string period, CancellationToken cancellationToken);

    /// <summary>Payrolls of a year (all when null) with their payslips, newest month first, not tracked.</summary>
    Task<IReadOnlyList<Payroll>> ListAsync(int? year, CancellationToken cancellationToken);

    /// <summary>Which of the periods have a finalized payroll (period lock).</summary>
    Task<IReadOnlySet<string>> GetFinalizedPeriodsAsync(IReadOnlyCollection<string> periods, CancellationToken cancellationToken);

    /// <summary>The payslip with its payroll, tracked.</summary>
    Task<LaborSettlement?> GetSettlementAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Payslips with their payroll, tracked.</summary>
    Task<IReadOnlyList<LaborSettlement>> GetSettlementsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Payslips matching the filter with their payroll, oldest month first, not tracked.</summary>
    Task<IReadOnlyList<LaborSettlement>> SearchSettlementsAsync(PayslipFilter filter, CancellationToken cancellationToken);

    /// <summary>Approved and Fully Paid payslips of an employee and the linked workers for a year, with payroll, not tracked.</summary>
    Task<IReadOnlyList<LaborSettlement>> ListFinalizedForEmployeeAsync(
        Guid employeeId, IReadOnlyCollection<Guid> workerIds, int year, CancellationToken cancellationToken);

    void Add(Payroll payroll);

    void AddSettlement(LaborSettlement settlement);

    void RemoveSettlement(LaborSettlement settlement);

    void AddLine(PayLine line);

    void RemoveLine(PayLine line);
}
