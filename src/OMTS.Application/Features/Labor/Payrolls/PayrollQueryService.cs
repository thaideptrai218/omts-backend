using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// What the payroll screens read (LABOR-12, LABOR-13, LABOR-14): the sheets with their totals, one sheet with its payslips, one payslip
/// with its jobs, payments and attendance, the payslips of an employee, the payments and the employees who can be paid. The Owner only
/// (the API checks the role). Each sheet is small, so the list is filtered by name, sorted and paged here after the database has summed the pay.
/// </summary>
public sealed class PayrollQueryService(
    IPayrollReader reader,
    IPayrollWorkReader workReader,
    IPayrollSheetRepository sheets,
    IAccountRepository accounts,
    IEmployeeRepository employees,
    IRaftRepository rafts)
{
    public const int MaxPageSize = 100;
    private const int MaxSearchLength = 200;
    private const int MaxPayableEmployees = 200;
    private static readonly string[] SortFields = ["code", "name", "from", "total", "paid", "status"];

    public async Task<Result<PayrollSheetPage>> ListAsync(PayrollListQuery query, CancellationToken cancellationToken)
    {
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var rows = await FilteredAsync(query.Search, query.PeriodType, query.Status, query.From, query.To, query.SortBy, query.SortDir, cancellationToken);
        if (!rows.IsSuccess)
        {
            return rows.Error!;
        }

        var all = rows.Value!;
        var summary = new PayrollTotals(all.Sum(r => r.Total), all.Sum(r => r.Paid), all.Sum(r => r.Outstanding));
        return new PayrollSheetPage(all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), all.Count, page, pageSize, summary);
    }

    /// <summary>The sheets as the list shows them, filtered and sorted, not paged.</summary>
    public async Task<Result<IReadOnlyList<PayrollSheetRow>>> FilteredAsync(
        string? search, PayrollPeriodType? type, IReadOnlyList<PayrollSheetStatus>? status, DateOnly? from, DateOnly? to,
        string? sortBy, string? sortDir, CancellationToken cancellationToken)
    {
        if (search?.Length > MaxSearchLength)
        {
            return AppError.Invalid("Search");
        }

        if (from > to)
        {
            return AppError.Invalid("Period");
        }

        var field = sortBy?.Trim().ToLowerInvariant() ?? "code";
        if (!SortFields.Contains(field))
        {
            return AppError.Invalid("Sort by");
        }

        if (sortDir is not null && !sortDir.Equals("asc", StringComparison.OrdinalIgnoreCase) && !sortDir.Equals("desc", StringComparison.OrdinalIgnoreCase))
        {
            return AppError.Invalid("Sort direction");
        }

        var headers = await reader.ListHeadersAsync(status is { Count: > 0 } ? PayrollRules.StatusIdsOf(status) : null, type, from, to, cancellationToken);
        var rows = headers.Select(h => new PayrollSheetRow(
            h.Id, h.Code, PayrollRules.NameOf(h.PeriodType, h.From, h.To), h.PeriodType, h.From, h.To, h.Total, h.Paid, h.Total - h.Paid,
            PayrollRules.StatusOf(h.StatusId), h.CreatedAt)).ToList();
        if (search?.Trim() is { Length: > 0 } text)
        {
            rows = rows.Where(r => r.Code.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var descending = sortDir is null ? field == "code" : sortDir.Equals("desc", StringComparison.OrdinalIgnoreCase);
        IEnumerable<PayrollSheetRow> ordered = field switch
        {
            "name" => Order(rows, r => r.Name, descending),
            "from" => Order(rows, r => r.From.DayNumber * 1000 + (r.To.DayNumber - r.From.DayNumber), descending),
            "total" => Order(rows, r => r.Total, descending),
            "paid" => Order(rows, r => r.Paid, descending),
            "status" => Order(rows, r => (int)r.Status, descending),
            _ => Order(rows, r => r.Code, descending),
        };
        return Result<IReadOnlyList<PayrollSheetRow>>.Success(ordered.ToList());
    }

    private static IEnumerable<PayrollSheetRow> Order<TKey>(IEnumerable<PayrollSheetRow> rows, Func<PayrollSheetRow, TKey> key, bool descending)
    {
        var comparer = typeof(TKey) == typeof(string) ? (IComparer<TKey>)(object)StringComparer.CurrentCultureIgnoreCase : Comparer<TKey>.Default;
        return descending
            ? rows.OrderByDescending(key, comparer).ThenByDescending(r => r.Code, StringComparer.Ordinal)
            : rows.OrderBy(key, comparer).ThenBy(r => r.Code, StringComparer.Ordinal);
    }

    public async Task<Result<PayrollSheetDetail>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var raw = await reader.GetDetailAsync(id, cancellationToken);
        if (raw is null)
        {
            return AppError.NotFound;
        }

        var createdBy = raw.CreatedBy is { } accountId
            ? (await accounts.GetDisplayNamesAsync([accountId], cancellationToken)).GetValueOrDefault(accountId)
            : null;
        var finalizedBy = raw.FinalizedBy is { } finalizerId
            ? (await accounts.GetDisplayNamesAsync([finalizerId], cancellationToken)).GetValueOrDefault(finalizerId)
            : null;
        var staff = await StaffOnAsync(raw.Slips.Select(s => s.EmployeeId).ToList(), raw.To, cancellationToken);
        var slips = raw.Slips.Select(s => Slip(s, staff.Contains(s.EmployeeId))).ToList();
        var total = slips.Sum(s => s.Net);
        var paid = slips.Sum(s => s.Paid);
        return new PayrollSheetDetail(
            raw.Id, raw.Code, PayrollRules.NameOf(raw.PeriodType, raw.From, raw.To), raw.PeriodType, raw.From, raw.To, raw.Scope, PayrollRules.StatusOf(raw.StatusId),
            raw.ExpenseMonth, raw.Note, createdBy, raw.CreatedAt, total, paid, total - paid, slips, createdBy, slips.Count, raw.UpdatedAt ?? raw.CreatedAt, finalizedBy, raw.FinalizedAt,
            await LeftOutAsync(raw, cancellationToken));
    }

    /// <summary>
    /// The employees this sheet would pay but another live sheet of an overlapping period already has (they are left out when a sheet is created or reloaded). Only the employees
    /// that sheet holds and this one does not are worked out, so the read stays small; a cancelled sheet lists none.
    /// </summary>
    private async Task<IReadOnlyList<PayrollLeftOut>> LeftOutAsync(PayrollDetailRaw raw, CancellationToken cancellationToken)
    {
        if (raw.StatusId == StatusIds.Payroll.Cancelled)
        {
            return [];
        }

        var onSheet = raw.Slips.Select(s => s.EmployeeId).ToHashSet();
        var holder = new Dictionary<Guid, Payroll>();
        foreach (var other in (await sheets.ListActiveOverlappingAsync(raw.From, raw.To, raw.Id, cancellationToken)).OrderBy(s => s.Code, StringComparer.Ordinal))
        {
            foreach (var employeeId in other.Lines.Select(l => l.EmployeeId).Concat(other.SelectedEmployees.Select(e => e.EmployeeId)))
            {
                holder.TryAdd(employeeId, other);
            }
        }

        var candidates = holder.Keys.Where(id => !onSheet.Contains(id) && (raw.Scope != PayrollScope.Selected || raw.SelectedEmployeeIds?.Contains(id) == true)).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var data = await workReader.LoadAsync(raw.From, raw.To, candidates, cancellationToken);
        return data.Employees.Where(e => PayrollService.Covers(raw.Scope, e, raw.To))
            .Select(e => PayrollCalculator.Calculate(e, raw.From, raw.To, raw.PeriodType, data))
            .Where(f => f.OnSheet && holder.ContainsKey(f.Employee.Id))
            .Select(f => new PayrollLeftOut(f.Employee.Id, f.Employee.FullName, holder[f.Employee.Id].Id, holder[f.Employee.Id].Code, holder[f.Employee.Id].PeriodStart, holder[f.Employee.Id].PeriodEnd))
            .OrderBy(l => l.EmployeeName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The employees among these who are Staff on <paramref name="day"/>: only they get adjustments.</summary>
    private async Task<IReadOnlySet<Guid>> StaffOnAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken) =>
        employeeIds.Count == 0
            ? new HashSet<Guid>()
            : (await workReader.GetEmployeesAsync(employeeIds, cancellationToken)).Where(e => WorkRules.TypeOn(e, day) == EmployeeType.Staff).Select(e => e.Id).ToHashSet();

    private static PayrollSlipRow Slip(PayrollSlipRaw s, bool allowsAdjustments)
    {
        var income = s.MainPay + s.ShiftPay + s.Bonus - s.ManualDeduction;
        var net = income - s.Deduction;
        return new PayrollSlipRow(
            s.Id, s.Code, s.EmployeeId, s.EmployeeCode ?? string.Empty, s.EmployeeName, s.SalaryType, s.Hours, s.Shifts, s.HourlyRateNote, s.MainPay, s.ShiftPay, s.Bonus, s.ManualDeduction,
            s.Deduction, income, net, s.Paid, net - s.Paid, s.Adjustments.Select(a => new SlipAdjustmentView(a.Kind, a.Amount, a.Reason)).ToList(), allowsAdjustments);
    }

    public async Task<Result<PayrollSlipDetail>> GetSlipAsync(Guid slipId, CancellationToken cancellationToken)
    {
        var raw = await reader.GetSlipAsync(slipId, cancellationToken);
        if (raw is null)
        {
            return AppError.NotFound;
        }

        var slip = Slip(raw.Slip, (await StaffOnAsync([raw.Slip.EmployeeId], raw.To, cancellationToken)).Contains(raw.Slip.EmployeeId));
        var jobs = raw.Jobs.Select(j => new PayrollSlipJobLine(j.JobName, j.Hours, j.HourlyRate, j.Amount, j.Kind, j.Shifts, j.ShiftRate)).ToList();
        var fixedPay = slip.MainPay - jobs.Where(j => j.Kind != PayLineKind.FieldShift).Sum(j => j.Amount);
        return new PayrollSlipDetail(
            slip.Id, slip.Code, raw.PayrollId, raw.PayrollCode, slip.EmployeeId, slip.EmployeeName, raw.From, raw.To, slip.Hours, slip.MainPay, slip.Bonus, slip.AdvanceDeduction,
            slip.Income, slip.Net, slip.Paid, slip.Outstanding, PayrollRules.StatusOf(raw.StatusId), jobs,
            raw.Payments.Select(p => new PayrollSlipPayment(p.Id, p.VoucherCode, p.PaidAt, p.Method, p.Amount, p.Kind, p.Cancelled ? "Cancelled" : "Recorded")).ToList(),
            await AttendanceAsync(slip.EmployeeId, raw.From, raw.To, raw.PeriodType, cancellationToken), slip.EmployeeCode,
            slip.SalaryType, slip.Shifts, slip.ShiftPay, slip.ManualDeduction, Math.Max(fixedPay, 0), slip.Adjustments, slip.AllowsAdjustments);
    }

    /// <summary>The days the employee was paid for in the period, one row per day and job type, as the payroll counts them (<see cref="PayrollCalculator"/>); a day without a complete record has none.</summary>
    private async Task<IReadOnlyList<PayrollSlipAttendanceDay>> AttendanceAsync(
        Guid employeeId, DateOnly from, DateOnly to, PayrollPeriodType periodType, CancellationToken cancellationToken)
    {
        var data = await workReader.LoadAsync(from, to, [employeeId], cancellationToken);
        if (data.Employees.Count == 0)
        {
            return [];
        }

        var figures = PayrollCalculator.Calculate(data.Employees[0], from, to, periodType, data);
        var recordOf = data.Attendances.Where(a => a.EmployeeId == employeeId).ToDictionary(a => a.Date);
        var periodsOf = data.Assignments.Where(a => a.EmployeeId == employeeId).ToLookup(a => a.Date);
        var raftCodes = (await rafts.GetByIdsAsync(data.Assignments.Where(a => a.RaftId is not null).Select(a => a.RaftId!.Value).Distinct().ToList(), cancellationToken))
            .ToDictionary(r => r.Id, r => r.Code);
        var hoursOf = new Dictionary<DateOnly, DayHours>();
        var days = new List<PayrollSlipAttendanceDay>();
        foreach (var paid in figures.Days.OrderBy(d => d.Date).ThenBy(d => d.JobName, StringComparer.CurrentCultureIgnoreCase))
        {
            var record = recordOf[paid.Date];
            if (!hoursOf.TryGetValue(paid.Date, out var day))
            {
                hoursOf[paid.Date] = day = WorkHoursCalculator.Calculate(
                    record.CheckInAt, record.CheckOutAt, periodsOf[paid.Date], AttendanceSettingsVersion.EffectiveOn(data.Settings, paid.Date));
            }

            var raftIds = day.Periods.Where(p => p.JobTypeId == paid.JobTypeId && p.RaftId is not null).Select(p => p.RaftId!.Value).Distinct().ToList();
            var codes = raftIds.Count == 0 ? null : string.Join(", ", raftIds.Select(id => raftCodes.GetValueOrDefault(id, string.Empty)).Where(c => c.Length > 0).Order());
            var hourly = paid.Kind == PayLineKind.Hourly;
            days.Add(new PayrollSlipAttendanceDay(
                paid.Date, paid.JobName, PayrollCalculator.HoursOf(paid.Minutes), codes, record.CheckInAt, record.CheckOutAt, hourly ? paid.Rate : 0, paid.Amount,
                paid.Kind, paid.Shifts, hourly ? 0 : paid.Rate));
        }

        return days;
    }

    /// <summary>The payslips of one employee, with what is owed on the Finalized ones and the advances not deducted yet (404 for an employee that does not exist).</summary>
    public async Task<Result<EmployeeSlipPage>> EmployeeSlipsAsync(Guid employeeId, EmployeePayslipListQuery query, CancellationToken cancellationToken)
    {
        if (await employees.GetByIdAsync(employeeId, cancellationToken) is null)
        {
            return AppError.NotFound;
        }

        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var raw = await reader.ListEmployeeSlipsAsync(
            employeeId, query.Status is { } status ? PayrollRules.StatusIdsOf([status]) : null, page, pageSize, cancellationToken);
        var items = raw.Items.Select(s => new EmployeeSlipRow(
            s.Id, s.Code, s.PayrollId, s.PayrollCode, PayrollRules.NameOf(s.PeriodType, s.From, s.To), s.From, s.To, s.Income, s.Net, s.Paid, s.Net - s.Paid,
            PayrollRules.StatusOf(s.StatusId), s.FinalizedAt)).ToList();
        return new EmployeeSlipPage(items, raw.Total, page, pageSize, new EmployeeSlipSummary(raw.Outstanding, raw.AdvanceBalance));
    }

    public async Task<Result<PayrollPaymentPage>> PaymentsAsync(PayrollPaymentListQuery query, CancellationToken cancellationToken)
    {
        if (query.Search?.Length > MaxSearchLength)
        {
            return AppError.Invalid("Search");
        }

        if (query.From > query.To)
        {
            return AppError.Invalid("Period");
        }

        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        if (!TryParse<PaymentMethod>(query.Method, out var method) || !TryParse<WagePaymentKind>(query.Kind, out var kind))
        {
            return AppError.Invalid(method is null && query.Method is not null ? "Method" : "Kind");
        }

        bool? cancelled = null;
        if (query.Status is not null)
        {
            cancelled = query.Status switch { "Recorded" => false, "Cancelled" => true, _ => null };
            if (cancelled is null)
            {
                return AppError.Invalid("Status");
            }
        }

        var (items, total, recorded) = await reader.ListPaymentsAsync(
            new PaymentFilter(query.Search?.Trim(), query.EmployeeId, query.PayrollId, query.From, query.To, method, kind, cancelled), page, pageSize, cancellationToken);
        return new PayrollPaymentPage(
            items.Select(p => new PayrollPaymentRow(
                p.Id, p.VoucherCode, p.PaidAt, p.EmployeeId, p.EmployeeName, p.PayrollId, p.PayrollCode, p.Method, p.Amount, p.Kind, p.Cancelled ? "Cancelled" : "Recorded",
                p.PayrollPeriodType is { } type && p.PayrollFrom is { } start && p.PayrollTo is { } end ? PayrollRules.NameOf(type, start, end) : null)).ToList(),
            total, page, pageSize, new PayrollPaymentSummary(recorded));
    }

    /// <summary>An enum name exactly as written (no numbers, no other case); null and empty mean "no filter".</summary>
    private static bool TryParse<T>(string? text, out T? value) where T : struct, Enum
    {
        value = null;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (!Enum.TryParse<T>(text, false, out var parsed) || !Enum.GetNames<T>().Contains(text))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public async Task<Result<PayableEmployees>> PayableEmployeesAsync(string? search, bool includeAll, CancellationToken cancellationToken)
    {
        if (search?.Length > MaxSearchLength)
        {
            return AppError.Invalid("Search");
        }

        var rows = await reader.ListPayableEmployeesAsync(search?.Trim(), includeAll, MaxPayableEmployees, cancellationToken);
        return new PayableEmployees(rows.Select(r => new PayableEmployee(r.EmployeeId, r.Code ?? string.Empty, r.Name, r.Outstanding)).ToList());
    }
}
