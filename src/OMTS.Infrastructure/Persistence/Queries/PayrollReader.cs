using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The read side of the payroll screens. The net pay of a payslip is its main pay and bonus less its deduction; what is paid on it is the sum
/// of its wage payments whose cash voucher is not cancelled, so a voucher cancelled in the cash book gives the money back to "still owed".
/// </summary>
internal sealed class PayrollReader(OmtsDbContext db, TimeProvider timeProvider) : IPayrollReader
{
    public async Task<IReadOnlyList<PayrollHeaderRow>> ListHeadersAsync(
        IReadOnlyCollection<int>? statusIds, PayrollPeriodType? type, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var sheets = db.Payrolls.AsNoTracking().AsQueryable();
        if (statusIds is not null)
        {
            sheets = sheets.Where(p => statusIds.Contains(p.StatusId));
        }

        if (type is { } periodType)
        {
            sheets = sheets.Where(p => p.PeriodType == periodType);
        }

        if (from is { } start)
        {
            sheets = sheets.Where(p => p.PeriodEnd >= start);
        }

        if (to is { } end)
        {
            sheets = sheets.Where(p => p.PeriodStart <= end);
        }

        return await sheets.Select(p => new PayrollHeaderRow(
            p.Id, p.Code, p.PeriodType, p.PeriodStart, p.PeriodEnd, p.StatusId, p.CreatedAt,
            db.PayrollLines.Where(l => l.PayrollId == p.Id).Sum(l => (decimal?)(l.MainPay + l.Bonus - l.Deduction)) ?? 0,
            db.WagePayments.Where(w => w.PayrollLine!.PayrollId == p.Id && w.CashVoucher.CancelledAt == null).Sum(w => (decimal?)w.Amount) ?? 0))
            .ToListAsync(cancellationToken);
    }

    public async Task<PayrollDetailRaw?> GetDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        var sheet = await db.Payrolls.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new { p.Id, p.Code, p.PeriodType, p.PeriodStart, p.PeriodEnd, p.Scope, p.StatusId, p.ExpenseMonth, p.Note, p.CreatedBy, p.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (sheet is null)
        {
            return null;
        }

        var slips = await SlipsAsync(db.PayrollLines.Where(l => l.PayrollId == id), cancellationToken);
        return new PayrollDetailRaw(
            sheet.Id, sheet.Code, sheet.PeriodType, sheet.PeriodStart, sheet.PeriodEnd, sheet.Scope, sheet.StatusId, sheet.ExpenseMonth, sheet.Note, sheet.CreatedBy, sheet.CreatedAt, slips);
    }

    private async Task<IReadOnlyList<PayrollSlipRaw>> SlipsAsync(IQueryable<PayrollLine> lines, CancellationToken cancellationToken)
    {
        var rows = await lines.AsNoTracking().Select(l => new PayrollSlipRaw(
            l.Id, l.Code, l.EmployeeId, l.Employee.Code, l.Employee.FullName, l.Hours, l.HourlyRateNote, l.MainPay, l.Bonus, l.Deduction,
            db.WagePayments.Where(w => w.PayrollLineId == l.Id && w.CashVoucher.CancelledAt == null).Sum(w => (decimal?)w.Amount) ?? 0))
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.EmployeeName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
    }

    public async Task<SlipDetailRaw?> GetSlipAsync(Guid slipId, CancellationToken cancellationToken)
    {
        var header = await db.PayrollLines.AsNoTracking().Where(l => l.Id == slipId)
            .Select(l => new { l.PayrollId, PayrollCode = l.Payroll.Code, From = l.Payroll.PeriodStart, To = l.Payroll.PeriodEnd, l.Payroll.StatusId })
            .FirstOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var slip = (await SlipsAsync(db.PayrollLines.Where(l => l.Id == slipId), cancellationToken))[0];
        var jobs = (await db.PayrollLineJobs.AsNoTracking().Where(j => j.PayrollLineId == slipId)
                .Select(j => new SlipJobRaw(j.JobName, j.Hours, j.HourlyRate, j.Amount)).ToListAsync(cancellationToken))
            .OrderBy(j => j.JobName, StringComparer.CurrentCultureIgnoreCase).ThenBy(j => j.HourlyRate).ToList();
        var payments = (await db.WagePayments.AsNoTracking().Where(w => w.PayrollLineId == slipId)
                .Select(w => new SlipPaymentRaw(w.CashVoucherId, w.CashVoucher.Code, w.PaidAt, w.Method, w.Amount, w.Kind, w.CashVoucher.CancelledAt != null))
                .ToListAsync(cancellationToken))
            .OrderBy(p => p.PaidAt).ThenBy(p => p.VoucherCode, StringComparer.Ordinal).ToList();
        return new SlipDetailRaw(slip, header.PayrollId, header.PayrollCode, header.From, header.To, header.StatusId, jobs, payments);
    }

    public async Task<EmployeeSlipPageRaw> ListEmployeeSlipsAsync(
        Guid employeeId, IReadOnlyCollection<int>? statusIds, int page, int pageSize, CancellationToken cancellationToken)
    {
        var lines = db.PayrollLines.AsNoTracking().Where(l => l.EmployeeId == employeeId);
        lines = statusIds is null
            ? lines.Where(l => l.Payroll.StatusId != StatusIds.Payroll.Cancelled)
            : lines.Where(l => statusIds.Contains(l.Payroll.StatusId));
        var total = await lines.CountAsync(cancellationToken);
        var items = await lines
            .OrderByDescending(l => l.Payroll.PeriodEnd).ThenByDescending(l => l.Payroll.PeriodStart).ThenByDescending(l => l.Code)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new EmployeeSlipRaw(
                l.Id, l.Code, l.PayrollId, l.Payroll.Code, l.Payroll.PeriodType, l.Payroll.PeriodStart, l.Payroll.PeriodEnd, l.Payroll.StatusId,
                l.MainPay + l.Bonus, l.MainPay + l.Bonus - l.Deduction,
                db.WagePayments.Where(w => w.PayrollLineId == l.Id && w.CashVoucher.CancelledAt == null).Sum(w => (decimal?)w.Amount) ?? 0))
            .ToListAsync(cancellationToken);

        var net = await db.PayrollLines
            .Where(l => l.EmployeeId == employeeId && l.Payroll.StatusId == StatusIds.Payroll.Finalized)
            .SumAsync(l => (decimal?)(l.MainPay + l.Bonus - l.Deduction), cancellationToken) ?? 0;
        var paid = await db.WagePayments
            .Where(w => w.EmployeeId == employeeId && w.Kind == WagePaymentKind.Wage && w.PayrollLine!.Payroll.StatusId == StatusIds.Payroll.Finalized && w.CashVoucher.CancelledAt == null)
            .SumAsync(w => (decimal?)w.Amount, cancellationToken) ?? 0;
        var advanced = await db.WagePayments
            .Where(w => w.EmployeeId == employeeId && w.Kind == WagePaymentKind.Advance && w.CashVoucher.CancelledAt == null)
            .SumAsync(w => (decimal?)w.Amount, cancellationToken) ?? 0;
        var deducted = await db.PayrollAdvanceDeductions
            .Where(d => d.Advance.EmployeeId == employeeId && d.Advance.CashVoucher.CancelledAt == null)
            .SumAsync(d => (decimal?)d.Amount, cancellationToken) ?? 0;
        return new EmployeeSlipPageRaw(items, total, net - paid, advanced - deducted);
    }

    public async Task<(IReadOnlyList<PaymentRaw> Items, int Total, decimal RecordedAmount)> ListPaymentsAsync(
        PaymentFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (search, employeeId, payrollId, from, to) = (filter.Search, filter.EmployeeId, filter.PayrollId, filter.From, filter.To);
        var payments = db.WagePayments.AsNoTracking().AsQueryable();
        if (payrollId is { } sheet)
        {
            payments = payments.Where(w => w.PayrollLine != null && w.PayrollLine.PayrollId == sheet);
        }

        if (employeeId is { } employee)
        {
            payments = payments.Where(w => w.EmployeeId == employee);
        }

        if (from is { } start)
        {
            payments = payments.Where(w => w.CashVoucher.VoucherDate >= start);
        }

        if (to is { } end)
        {
            payments = payments.Where(w => w.CashVoucher.VoucherDate <= end);
        }

        if (filter.Method is { } method)
        {
            payments = payments.Where(w => w.Method == method);
        }

        if (filter.Kind is { } kind)
        {
            payments = payments.Where(w => w.Kind == kind);
        }

        if (filter.Cancelled is { } cancelled)
        {
            payments = payments.Where(w => (w.CashVoucher.CancelledAt != null) == cancelled);
        }

        if (!string.IsNullOrEmpty(search))
        {
            payments = payments.Where(w => w.CashVoucher.Code.Contains(search) || w.Employee.FullName.Contains(search)
                || (w.PayrollLine != null && w.PayrollLine.Payroll.Code.Contains(search)));
        }

        var total = await payments.CountAsync(cancellationToken);
        var recorded = await payments.Where(w => w.CashVoucher.CancelledAt == null).SumAsync(w => (decimal?)w.Amount, cancellationToken) ?? 0m;
        var items = await payments
            .OrderByDescending(w => w.PaidAt).ThenByDescending(w => w.CashVoucher.Code).ThenByDescending(w => w.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(w => new PaymentRaw(
                w.CashVoucherId, w.CashVoucher.Code, w.PaidAt, w.EmployeeId, w.Employee.FullName,
                w.PayrollLine == null ? null : w.PayrollLine.PayrollId, w.PayrollLine == null ? null : w.PayrollLine.Payroll.Code,
                w.Method, w.Amount, w.Kind, w.CashVoucher.CancelledAt != null,
                w.PayrollLine == null ? null : w.PayrollLine.Payroll.PeriodType,
                w.PayrollLine == null ? null : w.PayrollLine.Payroll.PeriodStart,
                w.PayrollLine == null ? null : w.PayrollLine.Payroll.PeriodEnd))
            .ToListAsync(cancellationToken);
        return (items, total, recorded);
    }

    public async Task<IReadOnlyList<PayableEmployeeRaw>> ListPayableEmployeesAsync(string? search, bool includeAll, int take, CancellationToken cancellationToken)
    {
        var net = (await db.PayrollLines.Where(l => l.Payroll.StatusId == StatusIds.Payroll.Finalized)
                .GroupBy(l => l.EmployeeId).Select(g => new { Employee = g.Key, Amount = g.Sum(l => l.MainPay + l.Bonus - l.Deduction) }).ToListAsync(cancellationToken))
            .ToDictionary(r => r.Employee, r => r.Amount);
        var paid = (await db.WagePayments
                .Where(w => w.Kind == WagePaymentKind.Wage && w.PayrollLine!.Payroll.StatusId == StatusIds.Payroll.Finalized && w.CashVoucher.CancelledAt == null)
                .GroupBy(w => w.EmployeeId).Select(g => new { Employee = g.Key, Amount = g.Sum(w => w.Amount) }).ToListAsync(cancellationToken))
            .ToDictionary(r => r.Employee, r => r.Amount);
        var owed = net.Select(n => (Id: n.Key, Amount: n.Value - paid.GetValueOrDefault(n.Key))).Where(o => o.Amount > 0).ToDictionary(o => o.Id, o => o.Amount);

        var people = db.Employees.AsNoTracking().Include(e => e.TypePeriods).AsQueryable();
        var ids = owed.Keys.ToList();
        people = includeAll ? people.Where(e => e.StatusId == StatusIds.Employee.Working || ids.Contains(e.Id)) : people.Where(e => ids.Contains(e.Id));
        if (!string.IsNullOrEmpty(search))
        {
            people = people.Where(e => e.FullName.Contains(search) || (e.Code != null && e.Code.Contains(search)));
        }

        var today = timeProvider.Today();
        var found = await people.OrderBy(e => e.FullName).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        // Staff employees are not paid through OMTS (BR-LABOR-09): they are listed only for what they are still owed from the days they were seasonal workers.
        return found
            .Where(e => owed.ContainsKey(e.Id) || EmployeeTypePeriod.TypeOn(e.TypePeriods, today) != EmployeeType.Staff)
            .Take(take)
            .Select(e => new PayableEmployeeRaw(e.Id, e.Code, e.FullName, owed.GetValueOrDefault(e.Id)))
            .ToList();
    }
}
