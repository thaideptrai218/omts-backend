using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
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
    ICashAccountRepository accounts,
    IAccountRepository people,
    RowVersionGuard rowVersions,
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
        if (!await AccountAllowedAsync(request.Method, request.CashAccountId, cancellationToken))
        {
            invalid.Add("Cash account");
        }

        if (!await PaidByAllowedAsync(request.PaidById, null, cancellationToken))
        {
            invalid.Add("Paid by");
        }

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

        if (PaidBeforeFinalized(sheet, request.PaidAt!.Value) is { } early)
        {
            return early;
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

        var created = await SaveAsync(parts, request.PaidAt!.Value, request.Method!.Value, request.Note, requestKey, cancellationToken, null, request.CashAccountId, request.PaidById);
        return AsResult(created);
    }

    /// <summary>Pays an employee: the oldest Finalized payslips first (kind Wage, one voucher each), what is left over an Advance. Returns the id of the first voucher.</summary>
    public async Task<Result<StandalonePaymentResult>> PayEmployeeAsync(StandalonePayrollPaymentRequest request, string? requestKey, CancellationToken cancellationToken)
    {
        var invalid = InvalidFields(request.PaidAt, request.Method, request.Note, requestKey);
        if (!await AccountAllowedAsync(request.Method, request.CashAccountId, cancellationToken))
        {
            invalid.Add("Cash account");
        }

        if (!await PaidByAllowedAsync(request.PaidById, null, cancellationToken))
        {
            invalid.Add("Paid by");
        }

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

        foreach (var sheet in parts.Where(p => p.Line is not null).Select(p => p.Line!.Payroll).DistinctBy(p => p.Id))
        {
            if (PaidBeforeFinalized(sheet, request.PaidAt!.Value) is { } early)
            {
                return early;
            }
        }

        if (left > 0)
        {
            // An advance is for an employee who still works (seasonal or Staff): a Left employee is paid on the payslips only (BR-LABOR-01, BR-LABOR-11).
            if (employee.StatusId != StatusIds.Employee.Working)
            {
                var owed = open.Sum(l => l.Net - paid.GetValueOrDefault(l.Id));
                return new AppError(
                    ErrorKind.Validation, PayrollMessages.MoreThanOwed(employee.FullName, Math.Max(owed, 0)),
                    new Dictionary<string, object?> { ["field"] = "Amount", ["outstanding"] = Math.Max(owed, 0) });
            }

            parts.Add(new Part(null, left));
        }

        var created = await SaveAsync(parts, request.PaidAt!.Value, request.Method!.Value, request.Note, requestKey, cancellationToken, employee, request.CashAccountId, request.PaidById);
        return new StandalonePaymentResult(created[0].CashVoucherId);
    }

    /// <summary>The voucher of a wage or advance payment with what it paid. <paramref name="voucherId"/> is the id of its cash voucher.</summary>
    public async Task<Result<PayrollPaymentDetail>> GetAsync(Guid voucherId, CancellationToken cancellationToken) =>
        await payments.GetByVoucherForEditAsync(voucherId, cancellationToken) is { } payment ? await DetailAsync(payment, cancellationToken) : AppError.NotFound;

    /// <summary>Who may be named as having handed the money over: the Active Owner and staff accounts.</summary>
    public async Task<IReadOnlyList<PayrollPayer>> PayersAsync(CancellationToken cancellationToken) =>
        (await people.ListActiveInternalAsync(cancellationToken)).Select(a => new PayrollPayer(a.Id, a.DisplayName)).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>
    /// Edits when it was paid, by whom, the method (with the bank account) and the note of a Recorded voucher, on the payment and its cash voucher together; the amount, employee and payslip never change.
    /// A wage cannot be dated before its payroll was finalized. A newer version than the one sent answers MSG06.
    /// </summary>
    public async Task<Result<PayrollPaymentDetail>> UpdateAsync(Guid voucherId, UpdatePayrollPaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = await payments.GetByVoucherForEditAsync(voucherId, cancellationToken);
        if (payment is null)
        {
            return AppError.NotFound;
        }

        if (payment.CashVoucher.CancelledAt is not null)
        {
            return CancelledError(payment);
        }

        if (await rowVersions.CheckAsync(payment, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var voucher = payment.CashVoucher;
        var paidAt = (request.PaidAt ?? payment.PaidAt).ToOffset(BusinessClock.UtcOffset);
        var invalid = InvalidFields(request.PaidAt is null ? DateTimeOffset.UnixEpoch : paidAt, request.Method, request.Note, null);
        if (!await AccountAllowedAsync(request.Method, request.CashAccountId, cancellationToken) && request.CashAccountId != voucher.CashAccountId)
        {
            invalid.Add("Cash account");
        }

        if (!await PaidByAllowedAsync(request.PaidById, voucher.CollectedByAccountId, cancellationToken))
        {
            invalid.Add("Paid by");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (request.PaidAt is not null && payment.PayrollLine?.Payroll is { } sheet && PaidBeforeFinalized(sheet, paidAt) is { } early)
        {
            return early;
        }

        var before = Snapshot(payment);
        var method = request.Method!.Value;
        var fund = CashBookRules.FundOfPayment(method);
        var accountId = fund == CashFund.Bank ? request.CashAccountId : null;
        payment.PaidAt = paidAt;
        payment.Method = method;
        payment.Note = SupplierPaymentRules.Trimmed(request.Note);
        voucher.OccurredAt = paidAt;
        voucher.VoucherDate = BusinessClock.ToBusinessDate(paidAt);
        voucher.Fund = fund;
        voucher.CashAccountId = accountId;
        voucher.CashAccount = accountId is { } id ? (await accounts.GetByIdsAsync([id], cancellationToken)).GetValueOrDefault(id) : null;
        voucher.CollectedByAccountId = request.PaidById ?? voucher.CollectedByAccountId;
        voucher.Note = VoucherNote(payment.PayrollLine, request.Note);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(payment));
        if (changedAfter is not null)
        {
            unitOfWork.MarkChanged(payment);
            unitOfWork.MarkChanged(voucher);
            audit.Record(new AuditEntry("WagePayment.Edited", RecordType)
            {
                RecordId = payment.Id, RecordCode = voucher.Code, Before = changedBefore, After = changedAfter,
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await DetailAsync(payment, cancellationToken);
    }

    /// <summary>
    /// Cancels a Recorded voucher with its cash voucher: the payslip owes the money again, or the advance is no longer given. An advance that a later payroll already deducted cannot be cancelled (409).
    /// </summary>
    public async Task<Result<PayrollPaymentDetail>> CancelAsync(Guid voucherId, CancelPayrollPaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = await payments.GetByVoucherForEditAsync(voucherId, cancellationToken);
        if (payment is null)
        {
            return AppError.NotFound;
        }

        var voucher = payment.CashVoucher;
        if (voucher.CancelledAt is not null)
        {
            return CancelledError(payment);
        }

        var reason = SupplierPaymentRules.Trimmed(request.Reason);
        if (reason?.Length > PayrollRules.MaxNoteLength)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(payment, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (payment.Kind == WagePaymentKind.Advance && await payments.DeductedAsync(payment.Id, cancellationToken) is var deducted and > 0)
        {
            return new AppError(
                ErrorKind.Conflict, PayrollMessages.AdvanceAlreadyDeducted(voucher.Code, deducted), new Dictionary<string, object?> { ["reasonCode"] = "ADVANCE_DEDUCTED" });
        }

        voucher.CancelledAt = timeProvider.GetUtcNow();
        voucher.CancelReason = reason;
        unitOfWork.MarkChanged(voucher);
        unitOfWork.MarkChanged(payment);
        if (payment.PayrollLine is { } line)
        {
            // The payslip is touched, so a payment made on it at the same time meets a new row version.
            unitOfWork.MarkChanged(line);
        }

        audit.Record(new AuditEntry("WagePayment.Cancelled", RecordType)
        {
            RecordId = payment.Id,
            RecordCode = voucher.Code,
            Before = new { Status = CashEntryStatus.Recorded },
            After = new { Status = CashEntryStatus.Cancelled },
            Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await DetailAsync(payment, cancellationToken);
    }

    private static AppError CancelledError(WagePayment payment) =>
        AppError.Conflict(UserMessages.WrongStatus($"Payroll payment {payment.CashVoucher.Code}", CashEntryStatus.Cancelled.ToString()));

    private static object Snapshot(WagePayment p) => new
    {
        p.PaidAt, p.Method, p.Note, p.CashVoucher.CashAccountId, PaidBy = p.CashVoucher.CollectedByAccountId,
    };

    private async Task<PayrollPaymentDetail> DetailAsync(WagePayment payment, CancellationToken cancellationToken)
    {
        var voucher = payment.CashVoucher;
        var cancelled = voucher.CancelledAt is not null;
        var ids = new[] { payment.RecordedByAccountId, voucher.CollectedByAccountId ?? Guid.Empty }.Where(id => id != Guid.Empty).Distinct().ToList();
        var names = await people.GetDisplayNamesAsync(ids, cancellationToken);
        var account = voucher.CashAccount;
        var allocations = new List<PayrollPaymentAllocation>();
        var advanceDeducted = 0m;
        if (payment.PayrollLine is { } line)
        {
            var before = await payments.PaidBeforeAsync(payment, cancellationToken);
            var after = before + (cancelled ? 0 : payment.Amount);
            var sheet = line.Payroll;
            allocations.Add(new PayrollPaymentAllocation(
                line.Id, line.Code, sheet.Id, sheet.Code, PayrollRules.NameOf(sheet.PeriodType, sheet.PeriodStart, sheet.PeriodEnd), sheet.PeriodStart, sheet.PeriodEnd, sheet.FinalizedAt,
                line.Net, before, payment.Amount, after >= line.Net ? "Paid" : after > 0 ? "PartiallyPaid" : "Unpaid"));
        }
        else
        {
            advanceDeducted = await payments.DeductedAsync(payment.Id, cancellationToken);
        }

        return new PayrollPaymentDetail(
            voucher.Id, voucher.Code, payment.Kind, payment.EmployeeId, payment.Employee.Code, payment.Employee.FullName, payment.PaidAt, payment.Method,
            voucher.CashAccountId, account is null ? null : $"{account.ProviderName} - {account.AccountNumber}", payment.Note, payment.RecordedByAccountId, names.GetValueOrDefault(payment.RecordedByAccountId),
            payment.CreatedAt, voucher.CollectedByAccountId, voucher.CollectedByAccountId is { } paidBy ? names.GetValueOrDefault(paidBy) : null,
            cancelled ? "Cancelled" : "Recorded", voucher.CancelledAt, voucher.CancelReason, payment.Amount, RowVersionGuard.Encode(payment.RowVersion), allocations,
            payment.Kind == WagePaymentKind.Advance ? payment.Amount : 0, advanceDeducted);
    }

    /// <summary>Null means the signed-in account; otherwise an Active Owner or staff account (the one a voucher already names may stay).</summary>
    private async Task<bool> PaidByAllowedAsync(Guid? accountId, Guid? stored, CancellationToken cancellationToken) =>
        accountId is not { } id || id == stored || (await people.ListActiveInternalAsync(cancellationToken)).Any(a => a.Id == id);

    /// <summary>Makes the payments and their vouchers in one save; with a request key, a parallel save of the same key that won is answered with its payments.</summary>
    private async Task<IReadOnlyList<WagePayment>> SaveAsync(
        IReadOnlyList<Part> parts, DateTimeOffset paidAt, PaymentMethod method, string? note, string? requestKey, CancellationToken cancellationToken, Employee? employee = null, Guid? cashAccountId = null, Guid? paidById = null)
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
                CashAccountId = fund == CashFund.Bank ? cashAccountId : null,
                TypeId = CashVoucherTypeIds.PaymentPayroll,
                OccurredAt = occurredAt,
                VoucherDate = BusinessClock.ToBusinessDate(occurredAt),
                Amount = part.Amount,
                CollectedByAccountId = paidById ?? recorder,
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

    /// <summary>L16: wages are paid after the sheet is finalized; a payment dated before that day is refused (400, field Paid at).</summary>
    private static AppError? PaidBeforeFinalized(Payroll sheet, DateTimeOffset paidAt) =>
        sheet.FinalizedAt is { } finalized && BusinessClock.ToBusinessDate(paidAt) < BusinessClock.ToBusinessDate(finalized)
            ? new AppError(
                ErrorKind.Validation, PayrollMessages.PaidBeforeFinalized(sheet.Code, BusinessClock.ToBusinessDate(finalized)),
                new Dictionary<string, object?> { ["field"] = "Paid at", ["finalizedOn"] = BusinessClock.ToBusinessDate(finalized) })
            : null;

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

    /// <summary>The fund the money leaves: Cash takes no account; a bank transfer may name an active Bank account of the cooperative (none is allowed, as before the choice existed).</summary>
    private async Task<bool> AccountAllowedAsync(PaymentMethod? method, Guid? accountId, CancellationToken cancellationToken)
    {
        if (accountId is null)
        {
            return true;
        }

        if (method != PaymentMethod.BankTransfer)
        {
            return false;
        }

        var account = (await accounts.GetByIdsAsync([accountId.Value], cancellationToken)).GetValueOrDefault(accountId.Value);
        return account is { IsActive: true, Kind: CashAccountKind.Bank };
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
