using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>
/// PURCHASE-05 – money paid to a supplier or paid back by it, the edit of its voucher and the cancellation of one recorded by
/// mistake. A payment changes the supplier debt (BR-PURCHASE-04); it may name the receipts it pays, which only changes which
/// receipts the ledger reads as paid. The API checks the role: no zone, Owner and Accountant. Each save holds the payment, its
/// audit and the Owner's MSG-PURCHASE-04 (BR-PURCHASE-05).
/// The supplier row is part of each save and is read before the debt, so of two saves that read one debt only the first
/// succeeds and the other answers MSG06: the debt told to the Owner and both confirmations rest on a true reading.
/// </summary>
public sealed class SupplierPaymentService(
    ISupplierPaymentRepository payments,
    ISupplierRepository suppliers,
    SupplierDebtService debts,
    SupplierPaymentAllocationService allocations,
    SupplierPaymentNotifications notifications,
    PurchaseDocumentCodeService codes,
    IAccountRepository accounts,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(SupplierPayment);

    public async Task<Result<SupplierPaymentView>> RecordAsync(RecordSupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var key = SupplierPaymentRules.Trimmed(request.DeduplicationKey);
        if (key is not null && await FirstOfKeyAsync(key, cancellationToken) is { } first)
        {
            return first;
        }

        var now = timeProvider.GetUtcNow();
        var invalid = SupplierPaymentRules.InvalidFields(request, now);
        var supplier = request.SupplierId is { } supplierId ? await suppliers.GetForEditAsync(supplierId, cancellationToken) : null;
        if (supplier is null)
        {
            invalid.Insert(0, "Supplier");
        }

        if (request.PaidById is { } payerId && !await IsPayerAsync(payerId, cancellationToken))
        {
            invalid.Add("Paid by");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (await AllocationErrorAsync(supplier!.Id, request.Allocations, cancellationToken) is { } allocationError)
        {
            return allocationError;
        }

        // PURCHASE-05.1, then PURCHASE-05.4: one confirmation at a time, each answered by sending its flag with the same request.
        var debt = await debts.DebtAsync(supplier.Id, cancellationToken);
        if (SupplierPaymentRules.AdvanceConfirmation(request, supplier.Name, debt) is { } advance)
        {
            return advance;
        }

        var bankReference = request.Method == PaymentMethod.BankTransfer ? SupplierPaymentRules.Trimmed(request.BankReference) : null;
        if (bankReference is not null && !request.ConfirmDuplicateBankReference
            && await payments.FindRecordedByBankReferenceAsync(bankReference, cancellationToken) is { } existing)
        {
            return SupplierPaymentRules.DuplicateConfirmation(existing);
        }

        var kind = request.Kind!.Value;
        var amount = request.Amount!.Value;
        var paidAt = SupplierPaymentRules.PaidAt(request, now);
        // The typed code, or one taken once the payment is valid and confirmed, so a refused request spends no number; never given twice.
        var (code, duplicate) = await codes.ForNewAsync(PurchaseDocumentKind.Payment, request.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var payment = new SupplierPayment
        {
            Code = code,
            SupplierId = supplier.Id,
            Supplier = supplier,
            Kind = kind,
            Amount = amount,
            DiscountAmount = request.DiscountAmount,
            PaymentDate = BusinessClock.ToBusinessDate(paidAt),
            PaidAt = paidAt,
            PaidByAccountId = request.PaidById ?? currentUser.AccountId!.Value,
            Method = request.Method!.Value,
            BankReference = bankReference,
            Note = SupplierPaymentRules.Trimmed(request.Note),
            DeduplicationKey = key,
            RecordedByAccountId = currentUser.AccountId!.Value,
        };
        foreach (var part in request.Allocations ?? [])
        {
            payment.Allocations.Add(new SupplierPaymentAllocation { PaymentId = payment.Id, ReceiptId = part.ReceiptId!.Value, Amount = part.Amount!.Value });
        }

        payments.Add(payment);
        unitOfWork.MarkChanged(supplier);
        audit.Record(new AuditEntry("SupplierPayment.Recorded", RecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            After = new
            {
                payment.Kind, payment.SupplierId, payment.Amount, payment.DiscountAmount, payment.PaymentDate, payment.PaidAt, payment.PaidByAccountId,
                payment.Method, payment.BankReference, payment.Note,
                Allocations = payment.Allocations.Select(a => new { a.ReceiptId, a.Amount }).ToList(),
            },
        });
        var debtAfter = kind == SupplierPaymentKind.Payment ? debt - amount - request.DiscountAmount : debt + amount;
        await notifications.TransactionAsync(payment, supplier.Name, cancelled: false, debtAfter, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (key is not null && exception is DuplicateRecordException or ConcurrencyConflictException)
        {
            // A parallel request with the same key was saved first: this one created nothing. It loses on the key or on the
            // supplier row, whichever the save reaches first. With no payment of the key, another save won: MSG06, and the key stays free.
            if (await FirstOfKeyAsync(key, cancellationToken) is { } saved)
            {
                return saved;
            }

            throw;
        }

        return await ViewAsync(payment, debtAfter, cancellationToken);
    }

    /// <summary>
    /// The edit of a Recorded payment's voucher, by whoever may record payments: when and by whom it was paid, the method, the bank
    /// reference and the note. Its money, kind, supplier and receipts never change (sent otherwise: MSG-GEN-01); a new bank
    /// reference already recorded asks MSG-PURCHASE-06. A new time may move it to another date of the debt. Audited before and after.
    /// </summary>
    public async Task<Result<SupplierPaymentView>> UpdateAsync(Guid paymentId, UpdateSupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = await payments.GetForEditAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return AppError.NotFound;
        }

        if (payment.StatusId != StatusIds.SupplierPayment.Recorded)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Supplier payment {payment.Code}", statusCatalog.DisplayNameOf(payment.StatusId)));
        }

        if (await rowVersions.CheckAsync(payment, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var invalid = UnchangedMoneyErrors(payment, request);
        invalid.AddRange(SupplierPaymentRules.InvalidVoucher(request.Method, request.BankReference, request.Note));
        if (request.PaidAt is { } paidAt && !SupplierPaymentRules.NotInFuture(paidAt, timeProvider.GetUtcNow()))
        {
            invalid.Add("Paid at");
        }

        if (request.PaidById is { } payerId && payerId != payment.PaidByAccountId && !await IsPayerAsync(payerId, cancellationToken))
        {
            invalid.Add("Paid by");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var bankReference = request.Method == PaymentMethod.BankTransfer ? SupplierPaymentRules.Trimmed(request.BankReference) : null;
        if (bankReference is not null && bankReference != payment.BankReference && !request.ConfirmDuplicateBankReference
            && await payments.FindRecordedByBankReferenceAsync(bankReference, cancellationToken) is { } existing && existing.Id != payment.Id)
        {
            return SupplierPaymentRules.DuplicateConfirmation(existing);
        }

        var before = VoucherSnapshot(payment);
        if (request.PaidAt is { } newPaidAt)
        {
            payment.PaidAt = newPaidAt.ToOffset(BusinessClock.UtcOffset);
            payment.PaymentDate = BusinessClock.ToBusinessDate(payment.PaidAt);
        }

        payment.PaidByAccountId = request.PaidById ?? payment.PaidByAccountId;
        payment.Method = request.Method!.Value;
        payment.BankReference = bankReference;
        payment.Note = SupplierPaymentRules.Trimmed(request.Note);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, VoucherSnapshot(payment));
        if (changedAfter is not null)
        {
            unitOfWork.MarkChanged(payment);
            audit.Record(new AuditEntry("SupplierPayment.Edited", RecordType)
            {
                RecordId = payment.Id, RecordCode = payment.Code, Before = changedBefore, After = changedAfter,
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await ViewAsync(payment, null, cancellationToken);
    }

    /// <summary>The fields of an edit that may only be sent as stored.</summary>
    private static List<string> UnchangedMoneyErrors(SupplierPayment payment, UpdateSupplierPaymentRequest request)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.SameOrEmpty(request.Code, payment.Code))
        {
            invalid.Add("Code");
        }

        if (request.SupplierId is { } supplierId && supplierId != payment.SupplierId)
        {
            invalid.Add("Supplier");
        }

        if (request.Kind is { } kind && kind != payment.Kind)
        {
            invalid.Add("Kind");
        }

        if (request.Amount is { } amount && amount != payment.Amount)
        {
            invalid.Add("Amount");
        }

        if (request.DiscountAmount is { } discount && discount != payment.DiscountAmount)
        {
            invalid.Add("Discount amount");
        }

        if (request.Allocations is { } sent)
        {
            var stored = payment.Allocations.Select(a => (a.ReceiptId, a.Amount)).OrderBy(a => a.ReceiptId).ToList();
            var asSent = sent.Select(a => (ReceiptId: a?.ReceiptId ?? Guid.Empty, Amount: a?.Amount ?? 0)).OrderBy(a => a.ReceiptId).ToList();
            if (!stored.SequenceEqual(asSent))
            {
                invalid.Add("Allocations");
            }
        }

        return invalid;
    }

    private static object VoucherSnapshot(SupplierPayment p) => new { p.PaymentDate, p.PaidAt, p.PaidByAccountId, p.Method, p.BankReference, p.Note };

    /// <summary>Who may be named as having paid: the Active Owner and staff accounts, by name (as the receivers of a receipt).</summary>
    public async Task<IReadOnlyList<AccountRef>> PayersAsync(CancellationToken cancellationToken) =>
        (await accounts.ListActiveInternalAsync(cancellationToken)).Select(a => new AccountRef(a.Id, a.DisplayName)).ToList();

    /// <summary>Whether the account may be named as the payer: an Active Owner or staff account.</summary>
    private async Task<bool> IsPayerAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await accounts.ListActiveInternalAsync(cancellationToken)).Any(a => a.Id == accountId);

    /// <summary>
    /// Each receipt named must be a Completed receipt of the supplier with at least that much left today, as the debt detail lists
    /// it: MSG-GEN-01 on the first one that is not, with what it has left.
    /// </summary>
    private async Task<AppError?> AllocationErrorAsync(Guid supplierId, IReadOnlyList<PaymentAllocationInput>? parts, CancellationToken cancellationToken)
    {
        if (parts is not { Count: > 0 })
        {
            return null;
        }

        var unpaid = (await debts.UnpaidReceiptsAsync(supplierId, cancellationToken)).ToDictionary(u => u.Id);
        for (var i = 0; i < parts.Count; i++)
        {
            var field = $"Allocation {i + 1}";
            if (!unpaid.TryGetValue(parts[i].ReceiptId!.Value, out var receipt))
            {
                return AppError.Invalid(field, "Not a receipt of this supplier with money left to pay.");
            }

            if (parts[i].Amount > receipt.Remaining)
            {
                return AppError.Invalid(field, $"Receipt {receipt.Code} has {receipt.Remaining:0} VND left to pay.");
            }
        }

        return null;
    }

    /// <summary>PURCHASE-05.3: Recorded → Cancelled with a reason; it no longer counts in the debt, also in past periods.</summary>
    public async Task<Result<SupplierPaymentView>> CancelAsync(Guid paymentId, CancelSupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = await payments.GetForEditAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return AppError.NotFound;
        }

        if (payment.StatusId != StatusIds.SupplierPayment.Recorded)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Supplier payment {payment.Code}", statusCatalog.DisplayNameOf(payment.StatusId)));
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > SupplierPaymentRules.MaxNoteLength)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(payment, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var debt = await debts.DebtAsync(payment.SupplierId, cancellationToken);
        payment.StatusId = StatusIds.SupplierPayment.Cancelled;
        payment.CancelledAt = timeProvider.GetUtcNow();
        payment.CancelReason = reason;
        unitOfWork.MarkChanged(payment.Supplier);
        audit.Record(new AuditEntry("SupplierPayment.Cancelled", RecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.SupplierPayment.Recorded) },
            After = new { Status = statusCatalog.CodeOf(payment.StatusId) },
            Reason = reason,
        });
        // The payment leaves the debt: a payment raises it again, money paid back lowers it.
        var debtAfter = payment.Kind == SupplierPaymentKind.Payment ? debt + payment.Amount + payment.DiscountAmount : debt - payment.Amount;
        await notifications.TransactionAsync(payment, payment.Supplier.Name, cancelled: true, debtAfter, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(payment, debtAfter, cancellationToken);
    }

    public async Task<Result<SupplierPaymentView>> GetAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await payments.GetForEditAsync(paymentId, cancellationToken) is { } payment
            ? await ViewAsync(payment, null, cancellationToken)
            : AppError.NotFound;

    public async Task<SupplierPaymentPage> SearchAsync(SupplierPaymentQuery query, CancellationToken cancellationToken)
    {
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / 100), Math.Clamp(query.PageSize, 1, 100));
        var (items, total, paid, paidBack) = await payments.SearchAsync(
            query.SupplierId, query.Kind, query.Method, query.StatusId, query.From, query.To, page, pageSize, cancellationToken);
        var names = await accounts.GetDisplayNamesAsync(items.SelectMany(AccountIds).Distinct().ToList(), cancellationToken);
        return new SupplierPaymentPage(new Paged<SupplierPaymentView>(items.Select(p => View(p, names, null, null)).ToList(), page, pageSize, total), paid, paidBack);
    }

    private async Task<SupplierPaymentView?> FirstOfKeyAsync(string key, CancellationToken cancellationToken) =>
        await payments.FindByDeduplicationKeyAsync(key, cancellationToken) is { } first
            ? await ViewAsync(first, await debts.DebtAsync(first.SupplierId, cancellationToken), cancellationToken)
            : null;

    /// <summary>The payment with the receipts it pays, each as the ledger reads it now.</summary>
    private async Task<SupplierPaymentView> ViewAsync(SupplierPayment payment, decimal? debtAfter, CancellationToken cancellationToken)
    {
        var parts = await payments.AllocationsOfAsync(payment.Id, cancellationToken);
        var standing = parts.Count > 0
            ? await allocations.ReceiptStandingAsync(payment.SupplierId, payment.Id, timeProvider.Today(), cancellationToken)
            : new Dictionary<Guid, (decimal PaidByOthers, decimal Remaining)>();
        var views = parts.Select(a =>
        {
            var (paidBefore, remaining) = standing.GetValueOrDefault(a.ReceiptId);
            return new PaymentAllocationView(a.ReceiptId, a.ReceiptCode, a.ReceivedAt, a.ReceiptPayable, paidBefore, a.Amount, remaining);
        }).ToList();
        return View(payment, await accounts.GetDisplayNamesAsync(AccountIds(payment).Distinct().ToList(), cancellationToken), debtAfter, views);
    }

    private static IEnumerable<Guid> AccountIds(SupplierPayment p) =>
        p.PaidByAccountId is { } payer ? [p.RecordedByAccountId, payer] : [p.RecordedByAccountId];

    private SupplierPaymentView View(
        SupplierPayment p, IReadOnlyDictionary<Guid, string> names, decimal? debtAfter, IReadOnlyList<PaymentAllocationView>? allocationViews) => new(
        p.Id, p.Code, new SupplierRef(p.Supplier.Id, p.Supplier.Name, p.Supplier.PhoneNumber), p.Kind, p.Amount, p.PaymentDate, p.Method,
        p.BankReference, p.Note, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), p.CancelledAt, p.CancelReason,
        new AccountRef(p.RecordedByAccountId, names.GetValueOrDefault(p.RecordedByAccountId) ?? string.Empty),
        RowVersionGuard.Encode(p.RowVersion), debtAfter, p.DiscountAmount, p.PurchaseReturnId,
        p.PaidAt, p.PaidByAccountId is { } payer ? new AccountRef(payer, names.GetValueOrDefault(payer) ?? string.Empty) : null, allocationViews);
}
