using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// The + and − lines the Cooperative Owner enters on the payslip of an internal (Staff) employee while the sheet is Provisional (the API checks the role). The whole list of a
/// payslip is saved at once, so a request is its own answer when repeated; the lines are kept through every recalculation of the sheet (<see cref="PayrollLineSync"/>).
/// A + line adds to the bonus, a − line to the manual deduction; the advance preview of the payslip follows its new income. Each save holds the sheet and its audit record.
/// </summary>
public sealed class PayrollAdjustmentService(
    IPayrollSheetRepository sheets,
    IPayrollWorkReader work,
    IWagePaymentRepository payments,
    PayrollQueryService queries,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(Payroll);

    public async Task<Result<PayrollSheetDetail>> SaveAsync(Guid slipId, SaveSlipAdjustmentsRequest request, CancellationToken cancellationToken)
    {
        var items = request.Items;
        if (items is null || items.Count > PayrollRules.MaxAdjustments)
        {
            return AppError.Invalid("Adjustments");
        }

        var invalid = new List<string>();
        var wanted = new List<(PayrollAdjustmentKind Kind, decimal Amount, string Reason)>();
        foreach (var item in items)
        {
            var reason = item.Reason?.Trim() ?? string.Empty;
            if (item.Kind is not { } kind || !Enum.IsDefined(kind))
            {
                invalid.Add("Adjustment kind");
            }
            else if (!PayrollRules.IsWholeAmount(item.Amount))
            {
                invalid.Add("Adjustment amount");
            }
            else if (reason.Length is 0 or > PayrollLineAdjustment.MaxReasonLength)
            {
                invalid.Add("Adjustment reason");
            }
            else
            {
                wanted.Add((kind, decimal.Truncate(item.Amount!.Value), reason));
            }
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        var sheet = await sheets.GetForEditByLineAsync(slipId, cancellationToken);
        if (sheet is null)
        {
            return AppError.NotFound;
        }

        if (!sheet.IsOpen)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Payroll {sheet.Code}", PayrollRules.StatusOf(sheet.StatusId).ToString()));
        }

        var line = sheet.Lines.First(l => l.Id == slipId);
        var employee = (await work.GetEmployeesAsync([line.EmployeeId], cancellationToken)).FirstOrDefault();
        if (employee is null || WorkRules.TypeOn(employee, sheet.PeriodEnd) != EmployeeType.Staff)
        {
            return AppError.Invalid("Employee", "Chỉ nhân viên nội bộ mới có khoản thưởng hoặc khấu trừ thủ công.");
        }

        var before = line.Adjustments.OrderBy(a => a.Position).Select(a => new { a.Kind, a.Amount, a.Reason }).ToList();
        var bonus = wanted.Where(a => a.Kind == PayrollAdjustmentKind.Bonus).Sum(a => a.Amount);
        var deduction = wanted.Where(a => a.Kind == PayrollAdjustmentKind.Deduction).Sum(a => a.Amount);
        var income = line.MainPay + line.ShiftPay + bonus - deduction;
        if (income < 0)
        {
            return AppError.Invalid("Adjustments", "Khấu trừ không được lớn hơn thu nhập của phiếu lương.");
        }

        foreach (var old in line.Adjustments.ToList())
        {
            line.Adjustments.Remove(old);
            sheets.Remove(old);
        }

        foreach (var (kind, amount, reason, position) in wanted.Select((a, i) => (a.Kind, a.Amount, a.Reason, i)))
        {
            var adjustment = new PayrollLineAdjustment { PayrollLineId = line.Id, Kind = kind, Amount = amount, Reason = reason, Position = position };
            line.Adjustments.Add(adjustment);
            sheets.Add(adjustment);
        }

        var balance = (await payments.ListOpenAdvancesAsync([line.EmployeeId], cancellationToken)).Sum(a => a.Remaining);
        PayrollLineSync.SumAdjustments(line);
        line.Deduction = PayrollLineSync.AdvanceDeductionOf(line.Income, balance);
        unitOfWork.MarkChanged(line);
        unitOfWork.MarkChanged(sheet);
        audit.Record(new AuditEntry("Payroll.AdjustmentsChanged", RecordType)
        {
            RecordId = sheet.Id,
            RecordCode = sheet.Code,
            Before = new { Slip = line.Code, Adjustments = before },
            After = new { Slip = line.Code, Adjustments = wanted.Select(a => new { a.Kind, a.Amount, a.Reason }).ToList(), line.Bonus, line.ManualDeduction },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.GetAsync(sheet.Id, cancellationToken);
    }
}
