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
/// finalizes it and cancels it. The pay is hours times the hourly rate in force (level A of the Owner); a sheet is made and calculated in
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
        var figures = data.Employees.Select(e => PayrollCalculator.Calculate(e, start, end, data)).ToList();
        var paid = figures.Where(f => f.Minutes > 0).ToList();
        if (paid.Count == 0)
        {
            return AppError.Invalid("Period", "No employee has hours in this period.");
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

    /// <summary>Recalculates a Provisional sheet: every line (the employees too) or only the hours and pay of its lines.</summary>
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
            && await OverlapAsync(sheet.PeriodStart, sheet.PeriodEnd, figures.Where(f => f.Minutes > 0).Select(f => f.Employee.Id).ToHashSet(), sheet.Id, cancellationToken) is { } overlap)
        {
            return overlap;
        }

        TouchEmployees(figures.Where(f => f.Minutes > 0));
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
    /// Provisional to Finalized (LABOR-13): the period has ended, no attendance of a seasonal worker is incomplete, no hours lack a rate and no
    /// request for a day of the period awaits review; the lines are recalculated and saved, the advances are taken off, and the records of the
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
        if (NotReady(sheet, figures, timeProvider.Today()) is { Count: > 0 } reasons)
        {
            return new AppError(ErrorKind.Validation, PayrollMessages.NotReady(reasons));
        }

        var employeeIds = sheet.Scope == PayrollScope.Selected ? sheet.SelectedEmployees.Select(s => s.EmployeeId).ToList() : null;
        if (await work.ListAwaitingRequestsAsync(sheet.PeriodStart, sheet.PeriodEnd, employeeIds, cancellationToken) is { Count: > 0 } waiting)
        {
            var list = string.Join(", ", waiting.Select(r => $"{r.Employee.FullName} {r.FromDate.ToString("dd/MM", CultureInfo.InvariantCulture)}").Distinct().Take(5));
            return AppError.Conflict(PayrollMessages.RequestsAwaitingReview(PayrollRules.NameOf(sheet.PeriodType, sheet.PeriodStart, sheet.PeriodEnd), waiting.Count, list));
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
        return data.Employees.Select(e => PayrollCalculator.Calculate(e, sheet.PeriodStart, sheet.PeriodEnd, data)).ToList();
    }

    /// <summary>Why the sheet cannot be finalized (MSG-LABOR-28); empty when it can.</summary>
    private static List<string> NotReady(Payroll sheet, IReadOnlyList<SlipFigures> figures, DateOnly today)
    {
        var reasons = new List<string>();
        if (sheet.PeriodEnd >= today)
        {
            reasons.Add($"the pay period ends on {sheet.PeriodEnd.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} and has not ended");
        }

        foreach (var f in figures.Where(f => f.IncompleteDays.Count > 0))
        {
            reasons.Add($"the attendance of {f.Employee.FullName} on {string.Join(", ", f.IncompleteDays.Take(5).Select(d => d.ToString("dd/MM", CultureInfo.InvariantCulture)))} is incomplete");
        }

        foreach (var f in figures.Where(f => f.UnratedDays.Count > 0))
        {
            var (day, job) = f.UnratedDays[0];
            reasons.Add($"{f.Employee.FullName}: the hours of {job} on {day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} have no hourly rate");
        }

        if (figures.All(f => f.Minutes == 0))
        {
            reasons.Add("no employee has hours in the period");
        }

        return reasons;
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
            ? new AppError(ErrorKind.Validation, UserMessages.Invalid("Period"), new Dictionary<string, object?> { ["field"] = "Period", ["existingId"] = other.Id, ["hint"] = $"Overlaps payroll {other.Code}." })
            : null;
    }

    /// <summary>Takes the advances of each employee off their payslip, oldest first, up to the payslip's income (BR-LABOR-11: the rest stays an advance for the next payroll).</summary>
    private async Task DeductAdvancesAsync(Payroll sheet, CancellationToken cancellationToken)
    {
        var open = (await payments.ListOpenAdvancesAsync(sheet.Lines.Select(l => l.EmployeeId).ToList(), cancellationToken))
            .GroupBy(a => a.Advance.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var line in sheet.Lines)
        {
            var room = line.Income;
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
