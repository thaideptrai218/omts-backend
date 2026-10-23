using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Advance deduction lines of Approved or Fully Paid payslips, summed per employee and per worker.</summary>
public sealed record DeductedAdvanceTotals(IReadOnlyDictionary<Guid, decimal> ByEmployee, IReadOnlyDictionary<Guid, decimal> ByWorker)
{
    public static readonly DeductedAdvanceTotals None = new(new Dictionary<Guid, decimal>(), new Dictionary<Guid, decimal>());
}

/// <summary>Wage payments and salary advances (LAB-09).</summary>
public interface ILaborPaymentRepository
{
    /// <summary>The payment with its payslip (and that payslip's payroll and payments) or its advance person, tracked.</summary>
    Task<LaborPayment?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every advance record (reversals included) of the people, with person and deducting payslip, tracked.</summary>
    Task<IReadOnlyList<LaborPayment>> ListAdvancesOfAsync(
        IReadOnlyCollection<Guid> employeeIds, IReadOnlyCollection<Guid> workerIds, CancellationToken cancellationToken);

    /// <summary>Every advance record (reversals included), with person and deducting payslip, not tracked.</summary>
    Task<IReadOnlyList<LaborPayment>> ListAllAdvancesAsync(CancellationToken cancellationToken);

    /// <summary>What finalized payrolls already deducted from the advances of the people.</summary>
    Task<DeductedAdvanceTotals> GetDeductedAdvanceTotalsAsync(
        IReadOnlyCollection<Guid> employeeIds, IReadOnlyCollection<Guid> workerIds, CancellationToken cancellationToken);

    void Add(LaborPayment payment);
}
