using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.Labor.Work;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// LABOR-14 for the Cooperative Owner: pays the payslips of a Finalized sheet, or pays an employee outside any sheet (oldest payslips first, the
/// rest an advance). Each payment is one wage payment and one cash voucher (a payment voucher of the Payroll type, party the employee) saved
/// together with the audit record, so the cash book shows it at once. A payslip that is paid meanwhile by another request is touched by this
/// save, which then meets a new row version and answers MSG06: the same send twice never pays twice. A client may also send an
/// Idempotency-Key, and a repeated request with it returns the payments already saved.
/// </summary>
public sealed class PayrollPaymentService(
    IPayrollSheetRepository sheets,
    IWagePaymentRepository payments,
    IEmployeeRepository employees,
    ICashVoucherRepository vouchers,
    ICashVoucherTypeRepository voucherTypes,
    CashVoucherCodeService codes,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(WagePayment);
    public const int MaxKeyLength = 64;

    /// <summary>One payment to make: a payslip (null for an advance) and what to pay on it.</summary>
    private sealed record Part(PayrollLine? Line, decimal Amount);

    public async Task<Result<PayrollPaymentResult>> PaySheetAsync(Guid payrollId, PayPayrollRequest request, string? requestKey, CancellationToken cancellationToken)
    {
        var invalid = InvalidFields(request.PaidAt, request.Method, request.Note, requestKey);
        var asked = request.Lines ?? [];
        if (asked.Count is 0 or > PayrollRules.MaxEmployees || asked.Any(l => l.SlipId is null || !IsMoney(l.Amount)) || asked.Select(l => l.SlipId).Distinct().Count() != asked.Count)
        {
            invalid.Add("Lines");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (requestKey is not null && await Existing(requestKey, cancellationToken) is { Count: > 0 } saved)
        {
            return AsResult(saved);
        }

        var sheet = await sheets.GetForEditAsync(payrollId, cancellationToken);
        if (sheet is null)
        {
            return AppError.NotFound;
        }

        if (sheet.StatusId != StatusIds.Payroll.Finalized)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Payroll {sheet.Code}", PayrollRules.StatusOf(sheet.StatusId).ToString()));
        }

        var byId = sheet.Lines.ToDictionary(l => l.Id);
        if (asked.Any(l => !byId.ContainsKey(l.SlipId!.Value)))
        {
            return AppError.Invalid("Lines");
        }

        var paid = await payments.PaidAmountsAsync(asked.Select(l => l.SlipId!.Value).ToList(), cancellationToken);
        var parts = new List<Part>();
        foreach (var ask in asked)
        {
            var line = byId[ask.SlipId!.Value];
            var owed = line.Net - paid.GetValueOrDefault(line.Id);
            if (ask.Amount!.Value > owed)
            {
                return new AppError(
                    ErrorKind.Validation, PayrollMessages.MoreThanOwed(line.Employee.FullName, Math.Max(owed, 0)),
                    new Dictionary<string, object?> { ["field"] = "Amount", ["slipId"] = line.Id, ["outstanding"] = Math.Max(owed, 0) });
            }

            parts.Add(new Part(line, ask.Amount.Value));
        }

        var created = await SaveAsync(parts, request.PaidAt!.Value, request.Method!.Value, request.Note, requestKey, cancellationToken);
        return AsResult(created);
    }

    /// <summary>Pays an employee: the oldest Finalized payslips first (kind Wage, one voucher each), what is left over an Advance. Returns the id of the first voucher.</summary>
    public async Task<Result<StandalonePaymentResult>> PayEmployeeAsync(StandalonePayrollPaymentRequest request, string? requestKey, CancellationToken cancellationToken)
    {
        var invalid = InvalidFields(request.PaidAt, request.Method, request.Note, requestKey);
        if (request.EmployeeId is null)
        {
            invalid.Insert(0, "Employee");
        }

        if (!IsMoney(request.Amount))
        {
            invalid.Add("Amount");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (requestKey is not null && await Existing(requestKey, cancellationToken) is { Count: > 0 } saved)
        {
            return new StandalonePaymentResult(saved[0].CashVoucherId);
        }

        var employee = await employees.GetForEditAsync(request.EmployeeId!.Value, cancellationToken);
        if (employee is null)
        {
            return AppError.Invalid("Employee");
        }

        var open = await payments.ListFinalizedLinesAsync(employee.Id, cancellationToken);
        var paid = await payments.PaidAmountsAsync(open.Select(l => l.Id).ToList(), cancellationToken);
        var left = request.Amount!.Value;
        var parts = new List<Part>();
        foreach (var line in open)
        {
            var take = Math.Min(left, line.Net - paid.GetValueOrDefault(line.Id));
            if (take > 0)
            {
                parts.Add(new Part(line, take));
                left -= take;
            }
        }

        if (left > 0)
        {
            // An advance is for a seasonal worker who still works: a Left employee is paid on the payslips only (BR-LABOR-01, BR-LABOR-11), and a Staff employee is not paid through OMTS (BR-LABOR-09).
            if (employee.StatusId != StatusIds.Employee.Working || WorkRules.TypeOn(employee, timeProvider.Today()) == EmployeeType.Staff)
            {
                var owed = open.Sum(l => l.Net - paid.GetValueOrDefault(l.Id));
                return new AppError(
                    ErrorKind.Validation, PayrollMessages.MoreThanOwed(employee.FullName, Math.Max(owed, 0)),
                    new Dictionary<string, object?> { ["field"] = "Amount", ["outstanding"] = Math.Max(owed, 0) });
            }

            parts.Add(new Part(null, left));
        }

        var created = await SaveAsync(parts, request.PaidAt!.Value, request.Method!.Value, request.Note, requestKey, cancellationToken, employee);
        return new StandalonePaymentResult(created[0].CashVoucherId);
    }

    /// <summary>Makes the payments and their vouchers in one save; with a request key, a parallel save of the same key that won is answered with its payments.</summary>
    private async Task<IReadOnlyList<WagePayment>> SaveAsync(
        IReadOnlyList<Part> parts, DateTimeOffset paidAt, PaymentMethod method, string? note, string? requestKey, CancellationToken cancellationToken, Employee? employee = null)
    {
        var type = (await voucherTypes.GetByIdsAsync([CashVoucherTypeIds.PaymentPayroll], cancellationToken)).GetValueOrDefault(CashVoucherTypeIds.PaymentPayroll);
        var fund = CashBookRules.FundOfPayment(method);
        var recorder = currentUser.AccountId!.Value;
        var made = new List<WagePayment>();
        foreach (var (part, index) in parts.Select((p, i) => (p, i)))
        {
            var who = part.Line?.Employee ?? employee!;
            var occurredAt = paidAt.ToOffset(BusinessClock.UtcOffset);
            var voucher = new CashVoucher
            {
                Code = await codes.NextAsync(fund, CashDirection.Payment, cancellationToken),
                Direction = CashDirection.Payment,
                Fund = fund,
                TypeId = CashVoucherTypeIds.PaymentPayroll,
                OccurredAt = occurredAt,
                VoucherDate = BusinessClock.ToBusinessDate(occurredAt),
                Amount = part.Amount,
                CollectedByAccountId = recorder,
                PartyKind = CashPartyKind.Employee,
                PartyId = who.Id,
                PartyName = who.FullName,
                Note = VoucherNote(part.Line, note),
                CountsInResult = type?.CountsInResult ?? true,
            };
            var payment = new WagePayment
            {
                Kind = part.Line is null ? WagePaymentKind.Advance : WagePaymentKind.Wage,
                EmployeeId = who.Id,
                PayrollLineId = part.Line?.Id,
                CashVoucherId = voucher.Id,
                CashVoucher = voucher,
                Amount = part.Amount,
                PaidAt = occurredAt,
                Method = method,
                Note = SupplierPaymentRules.Trimmed(note),
                RecordedByAccountId = recorder,
                RequestKey = requestKey,
                RequestSequence = index,
            };
            vouchers.Add(voucher);
            payments.Add(payment);
            if (part.Line is { } line)
            {
                // The payslip is touched, so a parallel payment of it meets a new row version and answers MSG06.
                unitOfWork.MarkChanged(line);
            }

            audit.Record(new AuditEntry("WagePayment.Recorded", RecordType)
            {
                RecordId = payment.Id,
                RecordCode = voucher.Code,
                After = new { payment.Kind, payment.EmployeeId, payment.PayrollLineId, payment.Amount, payment.PaidAt, payment.Method, VoucherId = voucher.Id },
            });
            made.Add(payment);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException) when (requestKey is not null)
        {
            // A parallel request with the same key saved first: this one created nothing.
            unitOfWork.DiscardChanges();
            return await Existing(requestKey, cancellationToken);
        }

        return made;
    }

    private async Task<IReadOnlyList<WagePayment>> Existing(string key, CancellationToken cancellationToken) =>
        await payments.ListByRequestKeyAsync(key, cancellationToken);

    private static Result<PayrollPaymentResult> AsResult(IReadOnlyList<WagePayment> made) => new PayrollPaymentResult(
        made.Select(p => new PayrollVoucherRef(p.CashVoucherId, p.CashVoucher.Code, p.PayrollLineId ?? Guid.Empty, p.Employee?.FullName ?? p.CashVoucher.PartyName ?? string.Empty, p.Amount)).ToList());

    private static string VoucherNote(PayrollLine? line, string? note)
    {
        var what = line is null ? "Tạm ứng lương" : $"Trả lương {line.Code} ({line.Payroll.Code})";
        var text = SupplierPaymentRules.Trimmed(note);
        return text is null ? what : $"{what}: {text}";
    }

    private List<string> InvalidFields(DateTimeOffset? paidAt, PaymentMethod? method, string? note, string? requestKey)
    {
        var invalid = new List<string>();
        if (paidAt is not { } at || !SupplierPaymentRules.NotInFuture(at, timeProvider.GetUtcNow()))
        {
            invalid.Add("Paid at");
        }

        if (method is not (PaymentMethod.Cash or PaymentMethod.BankTransfer))
        {
            invalid.Add("Method");
        }

        if (note?.Trim().Length > PayrollRules.MaxNoteLength)
        {
            invalid.Add("Note");
        }

        if (requestKey is not null && requestKey.Length > MaxKeyLength)
        {
            invalid.Add("Idempotency key");
        }

        return invalid;
    }

    /// <summary>Whole VND from 1 up to what the column holds.</summary>
    private static bool IsMoney(decimal? amount) =>
        amount is { } value && value >= 1 && value <= SupplierPaymentRules.MaxAmount && value == decimal.Truncate(value);
}
