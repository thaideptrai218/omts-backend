using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// DEBT-02.2 and DEBT-02.4 – the payments Awaiting confirmation, their confirmation in one list (with the payments cancelled from it) and the cancellation of one payment.
/// A confirmation changes no debt (BR-DEBT-02); a cancellation takes the payment out of the debt. A save that loses to a parallel one on a payment (confirmed or
/// cancelled meanwhile) or a customer profile reads again once, so the loser hears MSG-GEN-09 naming the payment; nothing of its list is saved. Settling a payment
/// without a customer (DEBT-02.6) comes with the bank webhook (PR 7), which records them.
/// </summary>
public sealed class CustomerPaymentStatusService(
    ICustomerPaymentRepository payments,
    CustomerDebtReader debts,
    CustomerPaymentStatusChanges statusChanges,
    CustomerPaymentNotifications notifications,
    CustomerPaymentViews views,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    IUnitOfWork unitOfWork)
{
    /// <summary>A lost race reads again once; a second loss is MSG06.</summary>
    public const int MaxAttempts = 2;

    private const int MaxPageSize = 100;

    /// <summary>DEBT-02.2 step 2: oldest first, those without a customer included; MSG-GEN-11 when there is none (DEBT-02.2.E1).</summary>
    public async Task<AwaitingCustomerPayments> AwaitingAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var (items, total, amount) = await payments.AwaitingConfirmationAsync(page, pageSize, cancellationToken);
        var empty = total == 0 ? UserMessages.NothingToShowYet : null;
        return new AwaitingCustomerPayments(
            new Paged<CustomerPaymentDetails>(await views.ViewAsync(items, cancellationToken), page, pageSize, total), amount, empty?.Text, empty?.Code);
    }

    /// <summary>DEBT-02.2 steps 3-6: every payment named must still be Awaiting confirmation; one notice to the Owner lists what was confirmed and cancelled.</summary>
    public async Task<Result<CustomerPaymentsConfirmed>> ConfirmAsync(ConfirmCustomerPaymentsRequest request, CancellationToken cancellationToken)
    {
        var confirmIds = request.Confirm ?? [];
        var cancels = request.Cancel ?? [];
        var ids = confirmIds.Concat(cancels.Select(c => c.Id)).ToList();
        if (ids.Count == 0 || ids.Count > CustomerPaymentRules.MaxBatchSize || ids.Distinct().Count() != ids.Count)
        {
            return AppError.Invalid("Payments");
        }

        var reasons = new Dictionary<Guid, string>();
        foreach (var cancel in cancels)
        {
            if (CustomerPaymentRules.Reason(cancel.Reason) is not { } reason)
            {
                return new AppError(ErrorKind.Validation, UserMessages.Invalid("Reason"), new Dictionary<string, object?> { ["field"] = "Reason", ["paymentId"] = cancel.Id });
            }

            reasons[cancel.Id] = reason;
        }

        for (var attempt = 1; ; attempt++)
        {
            var found = (await payments.GetForEditAsync(ids, cancellationToken)).ToDictionary(p => p.Id);
            if (found.Count != ids.Count)
            {
                return AppError.NotFound;
            }

            if (ids.Select(id => found[id]).FirstOrDefault(p => p.StatusId != StatusIds.CustomerPayment.AwaitingConfirmation) is { } done)
            {
                return WrongStatus(done);
            }

            // DEBT-02.2 step 3: a transfer whose code matches no saved invoice is settled (DEBT-02.6, PR 7b), not confirmed plainly, so it stays in this list. Cancelling it stays
            // allowed; a walk-in payment of an invoice is confirmed as any other.
            if (confirmIds.Select(id => found[id]).FirstOrDefault(NeedsSettlement) is { } unsettled)
            {
                return AppError.Conflict(
                    UserMessages.WrongStatus($"Customer payment {unsettled.Code}", NeedsSettlementStatus),
                    new Dictionary<string, object?> { ["paymentId"] = unsettled.Id, ["needsSettlement"] = true });
            }

            var confirmed = confirmIds.Select(id => found[id]).ToList();
            var cancelled = cancels.Select(c => found[c.Id]).ToList();
            var debtsAfter = await DebtsAfterCancellingAsync(cancelled, cancellationToken);
            confirmed.ForEach(statusChanges.Confirm);
            cancelled.ForEach(p => statusChanges.Cancel(p, reasons[p.Id]));
            await notifications.BatchAsync(confirmed, cancelled, debtsAfter, cancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                var message = UserMessages.Saved("Confirmation of the payments");
                return new CustomerPaymentsConfirmed(
                    await views.ViewAsync(confirmed, cancellationToken), await views.ViewAsync(cancelled, cancellationToken), message.Text, message.Code);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                // Forget this attempt (its audit rows and its notice too) and read again: a payment changed meanwhile now answers MSG-GEN-09.
                unitOfWork.DiscardChanges();
            }
        }
    }

    /// <summary>DEBT-02.4 on its own: a payment or money paid back that is not cancelled, with a reason; the Owner is told at once.</summary>
    public async Task<Result<CustomerPaymentSaved>> CancelAsync(Guid paymentId, CancelCustomerPaymentRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var payment = await payments.GetForEditAsync(paymentId, cancellationToken);
            if (payment is null)
            {
                return AppError.NotFound;
            }

            if (payment.StatusId == StatusIds.CustomerPayment.Cancelled)
            {
                return WrongStatus(payment);
            }

            if (CustomerPaymentRules.Reason(request.Reason) is not { } reason)
            {
                return AppError.Invalid("Reason");
            }

            if (request.RowVersion is not null && await rowVersions.CheckAsync(payment, request.RowVersion, cancellationToken) is { } conflict)
            {
                return conflict;
            }

            var debtAfter = (await DebtsAfterCancellingAsync([payment], cancellationToken)).Select(d => (decimal?)d.Debt).SingleOrDefault();
            statusChanges.Cancel(payment, reason);
            await notifications.TransactionAsync(payment, cancelled: true, debtAfter, cancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return await views.SavedAsync(payment, debtAfter, cancelled: true, cancellationToken);
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts)
            {
                unitOfWork.DiscardChanges();
            }
        }
    }

    /// <summary>
    /// The debt of each profile once these payments are cancelled, read before the change; each profile is marked changed, so a parallel payment or cancellation that
    /// read the same debt cannot also be saved. Payments without a profile count in no debt.
    /// </summary>
    private async Task<IReadOnlyList<(string CustomerName, decimal Debt)>> DebtsAfterCancellingAsync(IReadOnlyList<CustomerPayment> cancelled, CancellationToken cancellationToken)
    {
        var ofProfiles = cancelled.Where(p => p.Customer is not null).GroupBy(p => p.Customer!).ToList();
        var before = await debts.DebtsAsync(ofProfiles.Select(g => g.Key.Id).ToList(), cancellationToken);
        var after = new List<(string CustomerName, decimal Debt)>();
        foreach (var group in ofProfiles)
        {
            unitOfWork.MarkChanged(group.Key);
            after.Add((group.Key.Name, group.Aggregate(before[group.Key.Id], (debt, p) => CustomerPaymentRules.DebtAfter(debt, p.Kind, p.Amount, cancelled: true))));
        }

        return after;
    }

    /// <summary>As MSG-GEN-09 names the state of a payment without a customer and without an invoice: "… is without a customer profile and cannot be used for this action."</summary>
    public const string NeedsSettlementStatus = "without a customer profile";

    /// <summary>A transfer recorded with no customer and no saved invoice (Stop sale, an unknown code, a short transfer): settled in DEBT-02.6, never confirmed plainly.</summary>
    public static bool NeedsSettlement(CustomerPayment payment) =>
        payment.CustomerId is null && payment.InvoiceId is null && payment.Kind == CustomerPaymentKind.Payment;

    /// <summary>MSG-GEN-09 naming the payment and its status (GBR-01).</summary>
    private AppError WrongStatus(CustomerPayment payment) => AppError.Conflict(
        UserMessages.WrongStatus($"Customer payment {payment.Code}", statusCatalog.DisplayNameOf(payment.StatusId)),
        new Dictionary<string, object?> { ["paymentId"] = payment.Id });
}
