using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.LaborCost;

/// <summary>
/// LAB-10 – staff (any employee account, the Owner included) see their own attendance, finalized payslips and advances,
/// read-only. The employee is the one linked to the caller's account; the records of seasonal workers linked to that
/// employee are included. Draft payslips, even their totals, are never returned; another person's payslip is not found.
/// </summary>
public sealed class PersonalPayService(
    IEmployeeRepository employees,
    IWorkforceRepository workforce,
    IAttendanceRepository attendance,
    IPayrollRepository payrolls,
    ILaborPaymentRepository payments,
    SalaryAdvanceService advances,
    PayslipViews views,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<Result<MyList<MyAttendanceItem>>> AttendanceAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? start.AddMonths(1).AddDays(-1);
        if (end < start || end.DayNumber - start.DayNumber > 366)
        {
            return AppError.Invalid("Date range");
        }

        var me = await MeAsync(cancellationToken);
        if (!me.IsSuccess)
        {
            return me.Error!;
        }

        var (employee, workerIds) = me.Value;
        var items = (await attendance.ListForEmployeeAsync(employee.Id, workerIds, start, end, cancellationToken))
            .Select(r => new MyAttendanceItem(r.WorkDate, r.Zone.Name, r.Result, r.Hours, r.Notes))
            .ToList();
        return List(items, "attendance");
    }

    public async Task<Result<MyList<MyPayslipListItem>>> PayslipsAsync(int? year, CancellationToken cancellationToken)
    {
        var me = await MeAsync(cancellationToken);
        if (!me.IsSuccess)
        {
            return me.Error!;
        }

        var (employee, workerIds) = me.Value;
        var items = (await payrolls.ListFinalizedForEmployeeAsync(employee.Id, workerIds, year ?? timeProvider.Today().Year, cancellationToken))
            .Select(s =>
            {
                var totals = PayslipViews.TotalsOf(s);
                return new MyPayslipListItem(
                    s.Id, s.Payroll.Period, s.StatusId == StatusIds.LaborSettlement.Paid ? "Fully Paid" : "Approved",
                    totals.Earnings, totals.Deductions, totals.AdvanceDeduction, s.TotalObligation, PayslipViews.Paid(s), PayslipViews.Remaining(s));
            })
            .ToList();
        return List(items, "payslips");
    }

    public async Task<Result<PayslipView>> PayslipAsync(Guid id, CancellationToken cancellationToken)
    {
        var me = await MeAsync(cancellationToken);
        if (!me.IsSuccess)
        {
            return me.Error!;
        }

        var (employee, workerIds) = me.Value;
        var payslip = await payrolls.GetSettlementAsync(id, cancellationToken);
        var mine = payslip is not null
            && (payslip.EmployeeId == employee.Id || (payslip.WorkerId is { } workerId && workerIds.Contains(workerId)))
            && payslip.StatusId != StatusIds.LaborSettlement.Draft;
        return mine ? await views.ViewAsync(payslip!, payslip!.Payroll.Period, cancellationToken) : AppError.NotFound;
    }

    public async Task<Result<MyList<AdvanceView>>> AdvancesAsync(CancellationToken cancellationToken)
    {
        var me = await MeAsync(cancellationToken);
        if (!me.IsSuccess)
        {
            return me.Error!;
        }

        var (employee, workerIds) = me.Value;
        var records = await payments.ListAdvancesOfAsync([employee.Id], workerIds, cancellationToken);
        return List(await advances.ViewsAsync(records, cancellationToken), "salary advances");
    }

    /// <summary>The employee linked to the caller's account and the workers linked to that employee; MSG51 when not exactly one.</summary>
    private async Task<Result<(Employee Employee, IReadOnlyList<Guid> WorkerIds)>> MeAsync(CancellationToken cancellationToken)
    {
        var employee = currentUser.AccountId is { } accountId ? await employees.GetByAccountIdAsync(accountId, cancellationToken) : null;
        if (employee is null)
        {
            return AppError.Conflict(UserMessages.AccountNotLinkedToEmployee);
        }

        return (employee, await workforce.GetWorkerIdsOfEmployeeAsync(employee.Id, cancellationToken));
    }

    private static MyList<T> List<T>(IReadOnlyList<T> items, string objectName) =>
        new(items, items.Count == 0 ? UserMessages.NoData(objectName).Text : null);
}
