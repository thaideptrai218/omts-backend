using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class PayrollSheetRepository(OmtsDbContext db) : IPayrollSheetRepository
{
    public Task<Payroll?> GetForEditAsync(Guid id, CancellationToken cancellationToken) =>
        db.Payrolls
            .Include(p => p.Lines).ThenInclude(l => l.Jobs)
            .Include(p => p.Lines).ThenInclude(l => l.Adjustments)
            .Include(p => p.Lines).ThenInclude(l => l.Employee)
            .Include(p => p.SelectedEmployees)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Payroll?> GetForEditByLineAsync(Guid lineId, CancellationToken cancellationToken) =>
        db.Payrolls
            .Include(p => p.Lines).ThenInclude(l => l.Jobs)
            .Include(p => p.Lines).ThenInclude(l => l.Adjustments)
            .Include(p => p.Lines).ThenInclude(l => l.Employee)
            .Include(p => p.SelectedEmployees)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Lines.Any(l => l.Id == lineId), cancellationToken);

    public async Task<IReadOnlyList<Payroll>> ListActiveOverlappingAsync(DateOnly from, DateOnly to, Guid? exceptId, CancellationToken cancellationToken) =>
        await db.Payrolls.AsNoTracking()
            .Include(p => p.Lines).Include(p => p.SelectedEmployees)
            .Where(p => p.StatusId != StatusIds.Payroll.Cancelled && p.PeriodStart <= to && p.PeriodEnd >= from && p.Id != exceptId)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    public void Add(Payroll payroll) => db.Payrolls.Add(payroll);

    public void Add(PayrollLine line) => db.PayrollLines.Add(line);

    public void Add(PayrollLineJob job) => db.PayrollLineJobs.Add(job);

    public void Add(PayrollLineAdjustment adjustment) => db.PayrollLineAdjustments.Add(adjustment);

    public void Remove(PayrollLine line) => db.PayrollLines.Remove(line);

    public void Remove(PayrollLineJob job) => db.PayrollLineJobs.Remove(job);

    public void Remove(PayrollLineAdjustment adjustment) => db.PayrollLineAdjustments.Remove(adjustment);
}

internal sealed class PayrollWorkReader(OmtsDbContext db) : IPayrollWorkReader
{
    public async Task<PayrollWorkData> LoadAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds, CancellationToken cancellationToken)
    {
        var attendance = db.Attendances.AsNoTracking().Where(a => a.Date >= from && a.Date <= to);
        if (employeeIds is not null)
        {
            attendance = attendance.Where(a => employeeIds.Contains(a.EmployeeId));
        }

        var attendances = await attendance.ToListAsync(cancellationToken);
        var ids = employeeIds ?? attendances.Select(a => a.EmployeeId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new PayrollWorkData([], [], [], [], new Dictionary<Guid, JobType>());
        }

        // Tracked, so a payroll that pays them can touch them (PayrollService); the records of the period are read as they are.
        List<Employee> employees;
        if (employeeIds is null)
        {
            // A sheet of everyone also holds the employees employed in the period who are paid with no attendance: a fixed salary, or no setup yet.
            var attended = ids;
            var candidates = await db.Employees.Include(e => e.TypePeriods).Include(e => e.SalarySetups).AsSplitQuery()
                .Where(e => attended.Contains(e.Id) || ((e.LastWorkingDay != null ? e.LastWorkingDay >= from : e.StatusId == StatusIds.Employee.Working) && (e.StartDate == null || e.StartDate <= to)))
                .ToListAsync(cancellationToken);
            employees = candidates.Where(e => attended.Contains(e.Id) || SalaryPlan.IsPaidWithoutAttendance(e, from, to)).ToList();
            ids = employees.Select(e => e.Id).ToList();
            if (ids.Count == 0)
            {
                return new PayrollWorkData([], [], [], [], new Dictionary<Guid, JobType>());
            }
        }
        else
        {
            employees = await db.Employees.Include(e => e.TypePeriods).Include(e => e.SalarySetups).AsSplitQuery().Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);
        }

        var assignments = await db.WorkAssignments.AsNoTracking()
            .Where(w => w.Date >= from && w.Date <= to && ids.Contains(w.EmployeeId)).ToListAsync(cancellationToken);
        var jobIds = assignments.Select(w => w.JobTypeId).Distinct().ToList();
        var jobTypes = await db.JobTypes.AsNoTracking().Include(j => j.Rates).Include(j => j.ShiftRates).AsSplitQuery().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, cancellationToken);
        var settings = await db.AttendanceSettingsVersions.AsNoTracking().ToListAsync(cancellationToken);
        return new PayrollWorkData(employees, attendances, assignments, settings, jobTypes);
    }

    public async Task<IReadOnlyList<Employee>> GetEmployeesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await db.Employees.AsNoTracking().Include(e => e.TypePeriods).Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EmployeeRequest>> ListAwaitingRequestsAsync(
        DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds, CancellationToken cancellationToken)
    {
        var requests = db.EmployeeRequests.AsNoTracking().Include(r => r.Employee)
            .Where(r => r.StatusId == StatusIds.EmployeeRequest.AwaitingReview && r.FromDate <= to && r.ToDate >= from);
        if (employeeIds is not null)
        {
            requests = requests.Where(r => employeeIds.Contains(r.EmployeeId));
        }

        return await requests.OrderBy(r => r.FromDate).ToListAsync(cancellationToken);
    }
}

internal sealed class WagePaymentRepository(OmtsDbContext db) : IWagePaymentRepository
{
    public async Task<IReadOnlyList<PayrollLine>> ListFinalizedLinesAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await db.PayrollLines.Include(l => l.Payroll).Include(l => l.Employee)
            .Where(l => l.EmployeeId == employeeId && l.Payroll.StatusId == StatusIds.Payroll.Finalized)
            .OrderBy(l => l.Payroll.PeriodStart).ThenBy(l => l.Payroll.CreatedAt).ThenBy(l => l.Code)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, decimal>> PaidAmountsAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        if (lineIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var rows = await db.WagePayments.AsNoTracking()
            .Where(w => w.PayrollLineId != null && lineIds.Contains(w.PayrollLineId.Value) && w.CashVoucher.CancelledAt == null)
            .GroupBy(w => w.PayrollLineId!.Value)
            .Select(g => new { Line = g.Key, Paid = g.Sum(w => w.Amount) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Line, r => r.Paid);
    }

    public async Task<IReadOnlyList<OpenAdvance>> ListOpenAdvancesAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        var advances = await db.WagePayments
            .Where(w => w.Kind == WagePaymentKind.Advance && employeeIds.Contains(w.EmployeeId) && w.CashVoucher.CancelledAt == null)
            .OrderBy(w => w.PaidAt).ThenBy(w => w.CreatedAt).ThenBy(w => w.Id)
            .ToListAsync(cancellationToken);
        var ids = advances.Select(a => a.Id).ToList();
        var deducted = (await db.PayrollAdvanceDeductions.AsNoTracking().Where(d => ids.Contains(d.AdvanceId))
                .GroupBy(d => d.AdvanceId).Select(g => new { Advance = g.Key, Amount = g.Sum(d => d.Amount) }).ToListAsync(cancellationToken))
            .ToDictionary(d => d.Advance, d => d.Amount);
        return advances.Select(a => new OpenAdvance(a, a.Amount - deducted.GetValueOrDefault(a.Id))).Where(a => a.Remaining > 0).ToList();
    }

    public async Task<WagePayment?> GetByVoucherForEditAsync(Guid voucherId, CancellationToken cancellationToken) =>
        await db.WagePayments.Include(w => w.CashVoucher).ThenInclude(v => v.CashAccount).Include(w => w.Employee)
            .Include(w => w.PayrollLine).ThenInclude(l => l!.Payroll)
            .FirstOrDefaultAsync(w => w.CashVoucherId == voucherId, cancellationToken);

    public async Task<decimal> PaidBeforeAsync(WagePayment payment, CancellationToken cancellationToken)
    {
        if (payment.PayrollLineId is not { } lineId)
        {
            return 0;
        }

        var others = await db.WagePayments.AsNoTracking()
            .Where(w => w.PayrollLineId == lineId && w.Id != payment.Id && w.CashVoucher.CancelledAt == null)
            .Select(w => new { w.PaidAt, w.CreatedAt, w.Amount })
            .ToListAsync(cancellationToken);
        return others.Where(w => w.PaidAt < payment.PaidAt || (w.PaidAt == payment.PaidAt && w.CreatedAt <= payment.CreatedAt)).Sum(w => w.Amount);
    }

    public async Task<decimal> DeductedAsync(Guid advanceId, CancellationToken cancellationToken) =>
        await db.PayrollAdvanceDeductions.Where(d => d.AdvanceId == advanceId).SumAsync(d => (decimal?)d.Amount, cancellationToken) ?? 0;

    public async Task<IReadOnlyList<WagePayment>> ListByRequestKeyAsync(string key, CancellationToken cancellationToken) =>
        await db.WagePayments.AsNoTracking().Include(w => w.CashVoucher).Include(w => w.Employee)
            .Where(w => w.RequestKey == key).OrderBy(w => w.RequestSequence).ToListAsync(cancellationToken);

    public void Add(WagePayment payment) => db.WagePayments.Add(payment);

    public void Add(PayrollAdvanceDeduction deduction) => db.PayrollAdvanceDeductions.Add(deduction);
}
