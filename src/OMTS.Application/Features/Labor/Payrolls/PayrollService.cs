using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// LABOR-12 and LABOR-13 for the Cooperative Owner, who does both (the API checks the role): creates a payroll sheet, recalculates it,
/// finalizes it and cancels it. The pay follows the salary setup of each employee on each day (<see cref="PayrollCalculator"/>); a sheet is made and calculated in
/// one step, so it is Provisional from its first save. Finalizing locks the records of the period (BR-LABOR-13: the sheet is Finalized, which
/// <see cref="PayPeriodLock"/> reads) and takes the advances off the payslips. Each save holds the sheet and its audit record.
/// </summary>
public sealed class PayrollService(
    IPayrollSheetRepository sheets,
    IPayrollWorkReader work,
    IWagePaymentRepository payments,
    PayrollLineSync lines,
    PayrollQueryService queries,
    IDocumentNumberAllocator numbers,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(Payroll);

    public async Task<Result<PayrollSheetDetail>> CreateAsync(CreatePayrollRequest request, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        var invalid = new List<string>();
        if (request.PeriodType is not { } type || !Enum.IsDefined(type))
        {
            invalid.Add("Period type");
        }

        if (request.Scope is not { } scope || !Enum.IsDefined(scope))
        {
            invalid.Add("Scope");
        }

        if (request.From is not { } from || request.To is not { } to
            || (request.PeriodType is { } t && !PayrollRules.IsValidPeriod(t, from, to)) || from > today)
        {
            invalid.Add("Period");
        }

        var chosen = request.EmployeeIds?.Distinct().ToList() ?? [];
        if (request.Scope == PayrollScope.Selected ? chosen.Count is 0 or > PayrollRules.MaxEmployees || chosen.Count != request.EmployeeIds!.Count : chosen.Count > 0)
        {
            invalid.Add("Employees");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var (periodType, periodScope, start, end) = (request.PeriodType!.Value, request.Scope!.Value, request.From!.Value, request.To!.Value);
        if (periodScope == PayrollScope.Selected && (await work.GetEmployeesAsync(chosen, cancellationToken)).Count != chosen.Count)
        {
            return AppError.Invalid("Employees");
        }

        var data = await work.LoadAsync(start, end, periodScope == PayrollScope.Selected ? chosen : null, cancellationToken);
        var figures = data.Employees.Select(e => PayrollCalculator.Calculate(e, start, end, periodType, data)).ToList();
        var paid = figures.Where(f => f.OnSheet).ToList();
        if (paid.Count == 0)
        {
            return AppError.Invalid("Period", "Kỳ này chưa có nhân viên nào có lương hoặc giờ công.");
        }

        if (await OverlapAsync(start, end, paid.Select(f => f.Employee.Id).ToHashSet(), null, cancellationToken) is { } overlap)
        {
            return overlap;
        }

        TouchEmployees(paid);
        var sheet = new Payroll
        {
            Code = PayrollRules.CodeOf(DocumentSequenceKeys.PayrollSheet, await numbers.NextAsync(DocumentSequenceKeys.PayrollSheet, cancellationToken)),
            PeriodType = periodType,
            Scope = periodScope,
            PeriodStart = start,
            PeriodEnd = end,
            StatusId = StatusIds.Payroll.Provisional,
        };
        foreach (var id in periodScope == PayrollScope.Selected ? chosen : [])
        {
            sheet.SelectedEmployees.Add(new PayrollSelectedEmployee { PayrollId = sheet.Id, EmployeeId = id });
        }

        await lines.SyncAsync(sheet, figures, dropEmpty: true, cancellationToken);
        sheets.Add(sheet);
        audit.Record(new AuditEntry("Payroll.Created", RecordType)
        {
            RecordId = sheet.Id,
            RecordCode = sheet.Code,
            After = new { sheet.PeriodType, sheet.Scope, sheet.PeriodStart, sheet.PeriodEnd, Status = PayrollSheetStatus.Provisional, Slips = sheet.Lines.Count, Total = sheet.Lines.Sum(l => l.Net) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.GetAsync(sheet.Id, cancellationToken);
    }

    /// <summary>Recalculates a Provisional sheet: every line (the employees too) or only the hours and pay of its lines. The adjustments of the lines stay.</summary>
    public async Task<Result<PayrollSheetDetail>> ReloadAsync(Guid id, ReloadPayrollRequest request, CancellationToken cancellationToken)
    {
        var sheet = await sheets.GetForEditAsync(id, cancellationToken);
        if (sheet is null)
        {
            return AppError.NotFound;
        }

        if (!sheet.IsOpen)
        {
            return WrongStatus(sheet);
        }

        if (request.Mode is not { } mode || !Enum.IsDefined(mode))
        {
            return AppError.Invalid("Mode");
        }

        var figures = await FiguresAsync(sheet, mode == PayrollReloadMode.All, cancellationToken);
        if (mode == PayrollReloadMode.All
            && await OverlapAsync(sheet.PeriodStart, sheet.PeriodEnd, figures.Where(f => f.OnSheet).Select(f => f.Employee.Id).ToHashSet(), sheet.Id, cancellationToken) is { } overlap)
        {
            return overlap;
        }

        TouchEmployees(figures.Where(f => f.OnSheet));
        await lines.SyncAsync(sheet, figures, dropEmpty: mode == PayrollReloadMode.All, cancellationToken);
        unitOfWork.MarkChanged(sheet);
        audit.Record(new AuditEntry("Payroll.Reloaded", RecordType)
        {
            RecordId = sheet.Id, RecordCode = sheet.Code, After = new { Mode = mode, Slips = sheet.Lines.Count, Total = sheet.Lines.Sum(l => l.Net) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.GetAsync(sheet.Id, cancellationToken);
    }

    /// <summary>
    /// Provisional to Finalized (LABOR-13): the period has ended, no attendance paid by the hour or the shift is incomplete, no work lacks a rate, no Staff employee lacks a salary
    /// setup, no payslip is negative and no request for a day of the period awaits review; the lines are recalculated and saved, the advances are taken off, and the records of the
    /// period are locked (BR-LABOR-13).
    /// </summary>
    public async Task<Result<PayrollSheetDetail>> FinalizeAsync(Guid id, FinalizePayrollRequest request, CancellationToken cancellationToken)
    {
        var sheet = await sheets.GetForEditAsync(id, cancellationToken);
        if (sheet is null)
        {
            return AppError.NotFound;
        }

        if (sheet.StatusId != StatusIds.Payroll.Provisional)
        {
            return WrongStatus(sheet);
        }

        if (!PayrollRules.IsExpenseMonth(request.ExpenseMonth))
        {
            return AppError.Invalid("Expense month");
        }

        var figures = await FiguresAsync(sheet, true, cancellationToken);
        if (NotReady(sheet, figures, timeProvider.Today()) is ({ Count: > 0 } reasons, var items))
        {
            return new AppError(ErrorKind.Validation, PayrollMessages.NotReady(reasons), new Dictionary<string, object?> { ["items"] = items });
        }

        var employeeIds = sheet.Scope == PayrollScope.Selected ? sheet.SelectedEmployees.Select(s => s.EmployeeId).ToList() : null;
        if (await work.ListAwaitingRequestsAsync(sheet.PeriodStart, sheet.PeriodEnd, employeeIds, cancellationToken) is { Count: > 0 } waiting)
        {
            var list = string.Join(", ", waiting.Select(r => $"{r.Employee.FullName} {PayrollMessages.ShortDate(r.FromDate)}").Distinct().Take(5));
            return AppError.Conflict(PayrollMessages.RequestsAwaitingReview(PayrollRules.NameOf(sheet.PeriodType, sheet.PeriodStart, sheet.PeriodEnd), waiting.Count, list));
        }

        // L11: the Owner approves the numbers on the screen. When the records changed since (an attendance corrected, a request approved), the recalculated totals differ: nothing is finalized, the new
        // numbers come back and the Owner confirms them. A request without the expected values finalizes as before.
        if (request.ExpectedTotal is not null || request.ExpectedSlips is not null)
        {
            var (slipsNow, totalNow) = await PreviewAsync(sheet, figures, cancellationToken);
            var viewedSlips = request.ExpectedSlips ?? sheet.Lines.Count;
            var viewedTotal = request.ExpectedTotal ?? sheet.Lines.Sum(l => l.Net);
            if (slipsNow != viewedSlips || Math.Abs(totalNow - viewedTotal) >= 0.5m)
            {
                return AppError.Conflict(
                    PayrollMessages.TotalsChanged(PayrollRules.NameOf(sheet.PeriodType, sheet.PeriodStart, sheet.PeriodEnd), viewedSlips, slipsNow, viewedTotal, totalNow),
                    new Dictionary<string, object?> { ["reasonCode"] = "TOTALS_CHANGED", ["currentTotal"] = totalNow, ["currentSlips"] = slipsNow, ["expectedTotal"] = viewedTotal, ["expectedSlips"] = viewedSlips });
            }
        }

        await lines.SyncAsync(sheet, figures, dropEmpty: true, cancellationToken);
        await DeductAdvancesAsync(sheet, cancellationToken);
        var now = timeProvider.GetUtcNow();
        sheet.StatusId = StatusIds.Payroll.Finalized;
        sheet.ExpenseMonth = request.ExpenseMonth;
        sheet.FinalizedAt = now;
        sheet.FinalizedByAccountId = currentUser.AccountId;
        unitOfWork.MarkChanged(sheet);
        audit.Record(new AuditEntry("Payroll.Finalized", RecordType)
        {
            RecordId = sheet.Id,
            RecordCode = sheet.Code,
            Before = new { Status = PayrollSheetStatus.Provisional },
            After = new { Status = PayrollSheetStatus.Finalized, sheet.ExpenseMonth, Slips = sheet.Lines.Count, Total = sheet.Lines.Sum(l => l.Net), Deducted = sheet.Lines.Sum(l => l.Deduction) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.GetAsync(sheet.Id, cancellationToken);
    }

    /// <summary>Creating or Provisional to Cancelled: the sheet no longer counts and leaves its period free. A Finalized sheet is never cancelled (LABOR-13).</summary>
    public async Task<Result<PayrollSheetDetail>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var sheet = await sheets.GetForEditAsync(id, cancellationToken);
        if (sheet is null)
        {
            return AppError.NotFound;
        }

        if (!sheet.IsOpen)
        {
            return WrongStatus(sheet);
        }

        var before = PayrollRules.StatusOf(sheet.StatusId);
        sheet.StatusId = StatusIds.Payroll.Cancelled;
        sheet.CancelledAt = timeProvider.GetUtcNow();
        sheet.CancelledByAccountId = currentUser.AccountId;
        unitOfWork.MarkChanged(sheet);
        audit.Record(new AuditEntry("Payroll.Cancelled", RecordType)
        {
            RecordId = sheet.Id, RecordCode = sheet.Code, Before = new { Status = before }, After = new { Status = PayrollSheetStatus.Cancelled },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.GetAsync(sheet.Id, cancellationToken);
    }

    /// <summary>
    /// Touches the employees a sheet pays, so that two sheets made at once for the same employee meet a new row version on the second save
    /// and the second answers MSG06 instead of paying the same days twice (the overlap check reads before either is saved).
    /// </summary>
    private void TouchEmployees(IEnumerable<SlipFigures> paid)
    {
        foreach (var figure in paid)
        {
            unitOfWork.MarkChanged(figure.Employee);
        }
    }

    private static AppError WrongStatus(Payroll sheet) =>
        AppError.Conflict(UserMessages.WrongStatus($"Payroll {sheet.Code}", PayrollRules.StatusOf(sheet.StatusId).ToString()));

    /// <summary>The figures of every employee the sheet covers: its chosen ones, or every employee with attendance (the sheet's lines only for a hours-only reload).</summary>
    private async Task<IReadOnlyList<SlipFigures>> FiguresAsync(Payroll sheet, bool resolveEmployees, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid>? ids = sheet.Scope == PayrollScope.Selected ? sheet.SelectedEmployees.Select(s => s.EmployeeId).ToList() : null;
        if (!resolveEmployees)
        {
            ids = sheet.Lines.Select(l => l.EmployeeId).ToList();
        }

        var data = await work.LoadAsync(sheet.PeriodStart, sheet.PeriodEnd, ids, cancellationToken);
        return data.Employees.Select(e => PayrollCalculator.Calculate(e, sheet.PeriodStart, sheet.PeriodEnd, sheet.PeriodType, data)).ToList();
    }

    /// <summary>
    /// Why the sheet cannot be finalized (MSG-LABOR-28), in Vietnamese with the names and dates; empty when it can. <see cref="NotReadyItem"/> carries the same reasons as data
    /// (the error's <c>items</c>), so the client can link each one to the screen that fixes it (correct attendance of an employee and day, a pay rate).
    /// </summary>
    internal static (List<string> Reasons, List<NotReadyItem> Items) NotReady(Payroll sheet, IReadOnlyList<SlipFigures> figures, DateOnly today)
    {
        var reasons = new List<string>();
        var items = new List<NotReadyItem>();
        if (sheet.PeriodEnd >= today)
        {
            reasons.Add($"kỳ lương chưa kết thúc (kết thúc ngày {PayrollMessages.Date(sheet.PeriodEnd)})");
            items.Add(new NotReadyItem("PeriodNotEnded", null, null, [sheet.PeriodEnd], null));
        }

        foreach (var f in figures.Where(f => f.IncompleteDays.Count > 0))
        {
            var days = string.Join(", ", f.IncompleteDays.Take(5).Select(PayrollMessages.ShortDate)) + (f.IncompleteDays.Count > 5 ? $" và {f.IncompleteDays.Count - 5} ngày khác" : string.Empty);
            reasons.Add($"{f.Employee.FullName}: thiếu giờ vào hoặc giờ ra ngày {days}");
            items.Add(new NotReadyItem("IncompleteAttendance", f.Employee.Id, f.Employee.FullName, f.IncompleteDays, null));
        }

        foreach (var f in figures.Where(f => f.UnratedDays.Count > 0))
        {
            foreach (var shift in new[] { false, true })
            {
                var unrated = f.UnratedDays.Where(u => (u.Kind != PayLineKind.Hourly) == shift).ToList();
                if (unrated.Count == 0)
                {
                    continue;
                }

                var (day, job) = (unrated[0].Day, unrated[0].JobName);
                reasons.Add($"{f.Employee.FullName}: {(shift ? "ca" : "giờ")} làm \"{job}\" ngày {PayrollMessages.Date(day)} chưa có đơn giá{(shift ? " ca" : string.Empty)}");
                items.Add(new NotReadyItem(shift ? "NoShiftRate" : "NoRate", f.Employee.Id, f.Employee.FullName, unrated.Select(u => u.Day).Distinct().ToList(), job));
            }
        }

        foreach (var f in figures.Where(f => f.MissingSetup))
        {
            reasons.Add($"{f.Employee.FullName}: Chưa thiết lập lương");
            items.Add(new NotReadyItem("NoSalarySetup", f.Employee.Id, f.Employee.FullName, [], null));
        }

        foreach (var line in sheet.Lines)
        {
            var figure = figures.FirstOrDefault(f => f.Employee.Id == line.EmployeeId);
            if ((figure?.MainPay ?? 0) + (figure?.ShiftPay ?? 0) + line.Bonus - line.ManualDeduction < 0)
            {
                reasons.Add($"{line.Employee.FullName}: khấu trừ lớn hơn thu nhập");
                items.Add(new NotReadyItem("NegativePay", line.EmployeeId, line.Employee.FullName, [], null));
            }
        }

        if (!figures.Any(f => f.OnSheet))
        {
            reasons.Add("kỳ này chưa có nhân viên nào có lương hoặc giờ công");
            items.Add(new NotReadyItem("NoHours", null, null, [], null));
        }

        return (reasons, items);
    }

    /// <summary>The slips and the total (net of advances) the sheet would show if reloaded now, worked out without changing anything (L11).</summary>
    private async Task<(int Slips, decimal Total)> PreviewAsync(Payroll sheet, IReadOnlyList<SlipFigures> figures, CancellationToken cancellationToken)
    {
        var kept = sheet.Lines.ToDictionary(l => l.EmployeeId);
        var paid = figures.Where(f => f.OnSheet || kept.GetValueOrDefault(f.Employee.Id)?.Adjustments.Count > 0).ToList();
        var balances = (await payments.ListOpenAdvancesAsync(paid.Select(f => f.Employee.Id).ToList(), cancellationToken))
            .GroupBy(a => a.Advance.EmployeeId).ToDictionary(g => g.Key, g => g.Sum(a => a.Remaining));
        var total = paid.Sum(f =>
        {
            var line = kept.GetValueOrDefault(f.Employee.Id);
            var income = f.MainPay + f.ShiftPay + (line?.Bonus ?? 0) - (line?.ManualDeduction ?? 0);
            return income - PayrollLineSync.AdvanceDeductionOf(income, balances.GetValueOrDefault(f.Employee.Id));
        });
        return (paid.Count, total);
    }

    /// <summary>
    /// 409 when another sheet that is not Cancelled has the same period and pays one of these employees (MSG-GEN-02, "already exists"); 400 when
    /// its period only overlaps. Sheets of other employees do not clash.
    /// </summary>
    private async Task<AppError?> OverlapAsync(DateOnly from, DateOnly to, IReadOnlySet<Guid> employeeIds, Guid? exceptId, CancellationToken cancellationToken)
    {
        var clashing = (await sheets.ListActiveOverlappingAsync(from, to, exceptId, cancellationToken))
            .Where(s => s.Lines.Select(l => l.EmployeeId).Concat(s.SelectedEmployees.Select(e => e.EmployeeId)).Any(employeeIds.Contains))
            .ToList();
        if (clashing.FirstOrDefault(s => s.PeriodStart == from && s.PeriodEnd == to) is { } same)
        {
            return AppError.AlreadyExists($"Payroll {same.Code}", same.Id, "Period");
        }

        return clashing.FirstOrDefault() is { } other
            ? new AppError(ErrorKind.Validation, UserMessages.Invalid("Period"), new Dictionary<string, object?> { ["field"] = "Period", ["existingId"] = other.Id, ["hint"] = $"Kỳ này trùng một phần với bảng lương {other.Code} ({PayrollMessages.Date(other.PeriodStart)} - {PayrollMessages.Date(other.PeriodEnd)})." })
            : null;
    }

    /// <summary>Takes the advances of each employee off their payslip, oldest first, up to the payslip's income (BR-LABOR-11: the rest stays an advance for the next payroll).</summary>
    private async Task DeductAdvancesAsync(Payroll sheet, CancellationToken cancellationToken)
    {
        var open = (await payments.ListOpenAdvancesAsync(sheet.Lines.Select(l => l.EmployeeId).ToList(), cancellationToken))
            .GroupBy(a => a.Advance.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var line in sheet.Lines)
        {
            var room = Math.Max(line.Income, 0);
            line.Deduction = 0;
            foreach (var advance in open.GetValueOrDefault(line.EmployeeId) ?? [])
            {
                var take = Math.Min(room, advance.Remaining);
                if (take <= 0)
                {
                    continue;
                }

                payments.Add(new PayrollAdvanceDeduction { AdvanceId = advance.Advance.Id, PayrollLineId = line.Id, Amount = take });
                // The advance is touched, so another sheet finalizing the same advance meanwhile meets a new row version and answers MSG06.
                unitOfWork.MarkChanged(advance.Advance);
                line.Deduction += take;
                room -= take;
            }
        }
    }
}
