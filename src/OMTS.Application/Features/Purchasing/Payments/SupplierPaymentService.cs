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
/// PURCHASE-05 – money paid to a supplier or paid back by it, and the cancellation of one recorded by mistake. Payments are not
/// linked to receipts and only change the supplier debt (BR-PURCHASE-04). The API checks the role: no zone, Owner and
/// Accountant. Each save holds the payment, its audit and the Owner's MSG-PURCHASE-04 (BR-PURCHASE-05).
/// The supplier row is part of each save and is read before the debt, so of two saves that read one debt only the first
/// succeeds and the other answers MSG06: the debt told to the Owner and both confirmations rest on a true reading.
/// </summary>
public sealed class SupplierPaymentService(
    ISupplierPaymentRepository payments,
    ISupplierRepository suppliers,
    SupplierDebtService debts,
    SupplierPaymentNotifications notifications,
    IDocumentNumberAllocator numbers,
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

        var invalid = SupplierPaymentRules.InvalidFields(request, timeProvider.Today());
        var supplier = request.SupplierId is { } supplierId ? await suppliers.GetForEditAsync(supplierId, cancellationToken) : null;
        if (supplier is null)
        {
            invalid.Insert(0, "Supplier");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        // PURCHASE-05.1, then PURCHASE-05.4: one confirmation at a time, each answered by sending its flag with the same request.
        var debt = await debts.DebtAsync(supplier!.Id, cancellationToken);
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
        // Taken once the payment is valid and confirmed, so a refused request spends no number; never given twice.
        var payment = new SupplierPayment
        {
            Code = DocumentCodes.PaymentCode(await numbers.NextAsync(DocumentSequenceKeys.SupplierPayment, cancellationToken)),
            SupplierId = supplier.Id,
            Supplier = supplier,
            Kind = kind,
            Amount = amount,
            PaymentDate = request.PaymentDate!.Value,
            Method = request.Method!.Value,
            BankReference = bankReference,
            Note = SupplierPaymentRules.Trimmed(request.Note),
            DeduplicationKey = key,
            RecordedByAccountId = currentUser.AccountId!.Value,
        };
        payments.Add(payment);
        unitOfWork.MarkChanged(supplier);
        audit.Record(new AuditEntry("SupplierPayment.Recorded", RecordType)
        {
            RecordId = payment.Id,
            RecordCode = payment.Code,
            After = new { payment.Kind, payment.SupplierId, payment.Amount, payment.PaymentDate, payment.Method, payment.BankReference, payment.Note },
        });
        var debtAfter = kind == SupplierPaymentKind.Payment ? debt - amount : debt + amount;
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
        var debtAfter = payment.Kind == SupplierPaymentKind.Payment ? debt + payment.Amount : debt - payment.Amount;
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
        var names = await accounts.GetDisplayNamesAsync(items.Select(p => p.RecordedByAccountId).Distinct().ToList(), cancellationToken);
        return new SupplierPaymentPage(new Paged<SupplierPaymentView>(items.Select(p => View(p, names, null)).ToList(), page, pageSize, total), paid, paidBack);
    }

    private async Task<SupplierPaymentView?> FirstOfKeyAsync(string key, CancellationToken cancellationToken) =>
        await payments.FindByDeduplicationKeyAsync(key, cancellationToken) is { } first
            ? await ViewAsync(first, await debts.DebtAsync(first.SupplierId, cancellationToken), cancellationToken)
            : null;

    private async Task<SupplierPaymentView> ViewAsync(SupplierPayment payment, decimal? debtAfter, CancellationToken cancellationToken) =>
        View(payment, await accounts.GetDisplayNamesAsync([payment.RecordedByAccountId], cancellationToken), debtAfter);

    private SupplierPaymentView View(SupplierPayment p, IReadOnlyDictionary<Guid, string> names, decimal? debtAfter) => new(
        p.Id, p.Code, new SupplierRef(p.Supplier.Id, p.Supplier.Name, p.Supplier.PhoneNumber), p.Kind, p.Amount, p.PaymentDate, p.Method,
        p.BankReference, p.Note, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), p.CancelledAt, p.CancelReason,
        new AccountRef(p.RecordedByAccountId, names.GetValueOrDefault(p.RecordedByAccountId) ?? string.Empty),
        RowVersionGuard.Encode(p.RowVersion), debtAfter);
}
