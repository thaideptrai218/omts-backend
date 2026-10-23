using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost;

/// <summary>The calculation of one payee plus what the payslip needs besides its lines.</summary>
/// <param name="AlwaysPayslip">An Employed employee with a MonthlyFixed rate valid in the month gets a payslip in any case.</param>
public sealed record PayeeComputation(PayeeCalculation Calculation, decimal OutstandingAdvances, bool AlwaysPayslip)
{
    public PayeeKey Payee => Calculation.Payee;
}

/// <summary>
/// LAB-07 – loads the month's attendance (all zones), confirmed piecework, pay rates and advances and runs
/// <see cref="PayrollCalculator"/> for every payee: everyone with attendance or confirmed piecework, every employee with a
/// MonthlyFixed rate valid in the month, and the payees of the payslips already in the payroll.
/// </summary>
public sealed class PayrollBuilder(
    IAttendanceRepository attendance,
    IWorkAssignmentRepository assignments,
    IPayRateRepository rates,
    IWorkforceRepository workforce,
    ILaborPaymentRepository payments)
{
    public async Task<IReadOnlyList<PayeeComputation>> ComputeAsync(
        PayrollPeriod period, IReadOnlyCollection<PayeeKey> existingPayees, CancellationToken cancellationToken)
    {
        var records = await attendance.ListInRangeAsync(period.Start, period.End, cancellationToken);
        var piecework = await assignments.ListConfirmedPieceworkAsync(period.Start, period.End, cancellationToken);
        var versions = await rates.ListOverlappingAsync(period.Start, period.End, cancellationToken);

        var payees = new HashSet<PayeeKey>(existingPayees);
        payees.UnionWith(records.Select(PersonOf));
        payees.UnionWith(piecework.Select(AssigneeOf));
        payees.UnionWith(versions.Where(v => v is { SalaryType: SalaryType.MonthlyFixed, EmployeeId: not null })
            .Select(v => new PayeeKey(PayeeKind.Employee, v.EmployeeId!.Value)));

        var employeeIds = Ids(payees, PayeeKind.Employee);
        var workerIds = Ids(payees, PayeeKind.Worker);
        var employees = (await workforce.GetEmployeesAsync(employeeIds, cancellationToken)).ToDictionary(e => e.Id);
        var advances = await payments.ListAdvancesOfAsync(employeeIds, workerIds, cancellationToken);
        var deducted = await payments.GetDeductedAdvanceTotalsAsync(employeeIds, workerIds, cancellationToken);

        var results = new List<PayeeComputation>();
        foreach (var payee in payees.OrderBy(p => p.Kind).ThenBy(p => p.Id))
        {
            var personRates = versions.Where(v => payee.Kind == PayeeKind.Employee ? v.EmployeeId == payee.Id : payee.Kind == PayeeKind.Worker && v.WorkerId == payee.Id).ToList();
            var employee = payee.Kind == PayeeKind.Employee ? employees.GetValueOrDefault(payee.Id) : null;
            var employed = employee is { StatusId: StatusIds.Employee.Working };
            var paysMonthly = employed || employee?.EmploymentEndDate >= period.Start;
            var input = new PayeeMonth(
                payee,
                personRates,
                records.Where(r => PersonOf(r) == payee).Select(r => new AttendanceDay(r.Id, r.WorkDate, r.Result)).ToList(),
                piecework.Where(p => AssigneeOf(p) == payee)
                    .Select(p => new ConfirmedPiecework(p.Id, p.CompletionDate!.Value, p.CompletedQuantity ?? 0, p.PieceRate ?? 0, p.Task, p.PieceUnit ?? string.Empty))
                    .ToList(),
                paysMonthly);
            var outstanding = payee.Kind == PayeeKind.Team
                ? 0
                : AdvanceLedger.OutstandingUpTo(
                    SalaryAdvances.Entries(SalaryAdvances.Of(advances, payee)), SalaryAdvances.Deducted(deducted, payee), period.End);
            var always = employed && personRates.Any(v => v.SalaryType == SalaryType.MonthlyFixed);
            results.Add(new PayeeComputation(PayrollCalculator.Calculate(period, input), outstanding, always));
        }

        return results;
    }

    /// <summary>Advances of one person paid up to the day and not yet deducted by a finalized payroll.</summary>
    public async Task<decimal> OutstandingAdvancesAsync(PayeeKey payee, DateOnly upTo, CancellationToken cancellationToken)
    {
        if (payee.Kind == PayeeKind.Team)
        {
            return 0;
        }

        IReadOnlyCollection<Guid> employeeIds = payee.Kind == PayeeKind.Employee ? [payee.Id] : [];
        IReadOnlyCollection<Guid> workerIds = payee.Kind == PayeeKind.Worker ? [payee.Id] : [];
        var advances = await payments.ListAdvancesOfAsync(employeeIds, workerIds, cancellationToken);
        var deducted = await payments.GetDeductedAdvanceTotalsAsync(employeeIds, workerIds, cancellationToken);
        return AdvanceLedger.OutstandingUpTo(SalaryAdvances.Entries(advances), SalaryAdvances.Deducted(deducted, payee), upTo);
    }

    public static PayeeKey PersonOf(AttendanceRecord r) =>
        r.EmployeeId is { } e ? new PayeeKey(PayeeKind.Employee, e) : new PayeeKey(PayeeKind.Worker, r.WorkerId!.Value);

    public static PayeeKey AssigneeOf(WorkAssignment a) =>
        a.EmployeeId is { } e ? new PayeeKey(PayeeKind.Employee, e)
        : a.WorkerId is { } w ? new PayeeKey(PayeeKind.Worker, w)
        : new PayeeKey(PayeeKind.Team, a.ContractorId!.Value);

    private static List<Guid> Ids(IEnumerable<PayeeKey> payees, PayeeKind kind) => payees.Where(p => p.Kind == kind).Select(p => p.Id).ToList();
}
