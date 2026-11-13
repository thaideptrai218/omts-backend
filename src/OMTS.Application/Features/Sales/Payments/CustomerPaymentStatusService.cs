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
/// DEBT-02.2, DEBT-02.4 and DEBT-02.6 – the payments Awaiting confirmation, their confirmation in one list (with the payments cancelled from it and the payments without a customer
/// settled in it, <see cref="CustomerPaymentSettlements"/>) and the cancellation of one payment. A confirmation changes no debt (BR-DEBT-02); a cancellation takes the payment out of the
/// debt; attaching a payment without a customer to a profile puts it in that debt. A save that loses to a parallel one on a payment (confirmed, cancelled or settled meanwhile) or a
/// customer profile reads again once, so the loser hears MSG-GEN-09 naming the payment; nothing of its list is saved. A payment without a customer cannot be confirmed plainly.
/// </summary>
public sealed class CustomerPaymentStatusService(
    ICustomerPaymentRepository payments,
    CustomerDebtReader debts,
    CustomerPaymentStatusChanges statusChanges,
    CustomerPaymentSettlements settlements,
    CustomerPaymentNotifications notifications,
    CustomerPaymentViews views,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    IUnitOfWork unitOfWork)
{
    /// <summary>A lost race reads again once; a second loss is MSG06.</summary>
    public const int MaxAttempts = 2;

    private const int MaxPageSize = 100;

    /// <summary>DEBT-02.2 step 2: oldest first, those without a customer included and marked <c>needsSettlement</c>; MSG-GEN-11 when there is none (DEBT-02.2.E1).</summary>
    public async Task<AwaitingCustomerPayments> AwaitingAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var (items, total, amount) = await payments.AwaitingConfirmationAsync(page, pageSize, cancellationToken);
        var empty = total == 0 ? UserMessages.NothingToShowYet : null;
        return new AwaitingCustomerPayments(
            new Paged<CustomerPaymentDetails>(await views.ViewAsync(items, cancellationToken), page, pageSize, total), amount, empty?.Text, empty?.Code);
    }

    /// <summary>DEBT-02.2 steps 3-6 and DEBT-02.6: every payment named must still be Awaiting confirmation; one notice to the Owner lists what was confirmed, cancelled and settled.</summary>
    public async Task<Result<CustomerPaymentsConfirmed>> ConfirmAsync(ConfirmCustomerPaymentsRequest request, CancellationToken cancellationToken)
    {
        var confirmIds = request.Confirm ?? [];
        var cancels = request.Cancel ?? [];
        var settles = request.Settle ?? [];

        // A malformed list entry is a field error (MSG-GEN-01), not a server error.
        if (cancels.Any(c => c is null) || settles.Any(s => s is null))
        {
            return AppError.Invalid("Payments");
        }

        var ids = confirmIds.Concat(cancels.Select(c => c.Id)).Concat(settles.Select(s => s.Id)).ToList();
        if (ids.Count == 0 || ids.Count > CustomerPaymentRules.MaxBatchSize || ids.Distinct().Count() != ids.Count)
        {
            return AppError.Invalid("Payments");
        }

        // DEBT-02.6 step 2: a customer profile (with its parts), or Pay money back.
        if (settles.FirstOrDefault(s => !CustomerPaymentSettlementRules.ChoosesOneWay(s) || (s.PayBack is not null && s.Allocations is not null)) is { } unclear)
        {
            return new AppError(ErrorKind.Validation, UserMessages.Invalid("Settlement"), new Dictionary<string, object?> { ["field"] = "Settlement", ["paymentId"] = unclear.Id });
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

            // DEBT-02.2 step 3: a transfer whose code matches no saved invoice is settled (DEBT-02.6), not confirmed plainly, so it stays in this list. Cancelling it stays
            // allowed; a walk-in payment of an invoice is confirmed as any other.
            if (confirmIds.Select(id => found[id]).FirstOrDefault(CustomerPaymentSettlementRules.NeedsSettlement) is { } unsettled)
            {
                return AppError.Conflict(
                    UserMessages.WrongStatus($"Customer payment {unsettled.Code}", CustomerPaymentSettlementRules.NeedsSettlementStatus),
                    new Dictionary<string, object?> { ["paymentId"] = unsettled.Id, ["needsSettlement"] = true });
            }

            var plan = await settlements.PlanAsync(settles, found, cancellationToken);
            if (!plan.IsSuccess)
            {
                return plan.Error!;
            }

            var confirmed = confirmIds.Select(id => found[id]).ToList();
            var cancelled = cancels.Select(c => found[c.Id]).ToList();
            var attached = plan.Value!.Items.Where(i => i.Customer is not null).Select(i => (Customer: i.Customer!, Amount: i.Payment.Amount)).ToList();
            var debtsAfter = await DebtsAfterAsync(cancelled, attached, cancellationToken);
            confirmed.ForEach(statusChanges.Confirm);
            cancelled.ForEach(p => statusChanges.Cancel(p, reasons[p.Id]));
            var settled = await settlements.ApplyAsync(plan.Value!, cancellationToken);
            await notifications.BatchAsync(
                confirmed, cancelled, settled.Select(s => s.Payment).ToList(), debtsAfter.Select(d => (d.Customer.Name, d.Debt)).ToList(), cancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                var message = UserMessages.Saved(confirmed.Count == 0 && cancelled.Count == 0 ? "Settlement of the payments" : "Confirmation of the payments");
                return new CustomerPaymentsConfirmed(
                    await views.ViewAsync(confirmed, cancellationToken), await views.ViewAsync(cancelled, cancellationToken), message.Text, message.Code,
                    await SettledViewsAsync(settled, debtsAfter, cancellationToken));
            }
            catch (Exception exception) when (exception is ConcurrencyConflictException or DuplicateRecordException && attempt < MaxAttempts)
            {
                // Forget this attempt (its audit rows and its notice too) and read again: a payment changed meanwhile now answers MSG-GEN-09. The unique index on money paid back
                // can fail before the row version of the payment does.
                unitOfWork.DiscardChanges();
            }
        }
    }

    /// <summary>The payments settled and their money paid back as the staff see them, read again once saved (the parts of an attached payment come with their invoices).</summary>
    private async Task<IReadOnlyList<CustomerPaymentSettled>> SettledViewsAsync(
        IReadOnlyList<AppliedSettlement> settled, IReadOnlyList<(Customer Customer, decimal Debt)> debtsAfter, CancellationToken cancellationToken)
    {
        if (settled.Count == 0)
        {
            return [];
        }

        var ids = settled.SelectMany(s => s.PaidBack is { } back ? new[] { s.Payment.Id, back.Id } : new[] { s.Payment.Id }).ToList();
        var details = (await views.ViewAsync(await payments.GetForEditAsync(ids, cancellationToken), cancellationToken)).ToDictionary(d => d.Id);
        return settled
            .Select(s => new CustomerPaymentSettled(
                details[s.Payment.Id],
                s.PaidBack is { } back ? details[back.Id] : null,
                s.Customer is { } customer ? debtsAfter.First(d => d.Customer.Id == customer.Id).Debt : (decimal?)null))
            .ToList();
    }

    /// <summary>The confirmation of one payment Awaiting confirmation (cash at the port): the same save as a list of one; 409 MSG-GEN-09 when it is no longer Awaiting confirmation.</summary>
    public async Task<Result<CustomerPaymentSaved>> ConfirmOneAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var confirmed = await ConfirmAsync(new ConfirmCustomerPaymentsRequest([paymentId]), cancellationToken);
        if (!confirmed.IsSuccess)
        {
            return confirmed.Error!;
        }

        var payment = confirmed.Value!.Confirmed[0];
        decimal? debt = payment.Customer is { } customer ? await debts.DebtAsync(customer.Id, cancellationToken) : null;
        var message = UserMessages.Saved($"Payment {payment.Code}");
        return new CustomerPaymentSaved(payment, debt, message.Text, message.Code);
    }

    /// <summary>
    /// DEBT-02.4 on its own: a payment or money paid back that is not cancelled (one waiting for its transfer too), with a reason; the Owner is told at once, unless nothing counted.
    /// A payment whose money paid back stands (DEBT-02.6) is cancelled after that money; cancelling the money paid back puts the payment back on the list, to be settled again.
    /// </summary>
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

            if (payment.Kind == CustomerPaymentKind.Payment && await payments.HasMoneyPaidBackAsync(payment.Id, cancellationToken))
            {
                return AppError.Conflict(
                    UserMessages.WrongStatus($"Customer payment {payment.Code}", CustomerPaymentSettlementRules.PaidBackStatus),
                    new Dictionary<string, object?> { ["paymentId"] = payment.Id, ["hasMoneyPaidBack"] = true });
            }

            if (payment.PaidBackForPaymentId is { } settledId
                && await payments.GetForEditAsync(settledId, cancellationToken) is { StatusId: StatusIds.CustomerPayment.Completed } settled)
            {
                statusChanges.UndoSettlement(settled, payment.Code);
            }

            var debtAfter = (await DebtsAfterAsync([payment], [], cancellationToken)).Select(d => (decimal?)d.Debt).SingleOrDefault();
            var counted = payment.StatusId != StatusIds.CustomerPayment.PendingTransfer;
            statusChanges.Cancel(payment, reason);
            if (counted)
            {
                await notifications.TransactionAsync(payment, cancelled: true, debtAfter, cancellationToken);
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return await views.SavedAsync(payment, debtAfter, cancelled: true, cancellationToken);
            }
            catch (Exception exception) when (exception is ConcurrencyConflictException or DuplicateRecordException && attempt < MaxAttempts)
            {
                unitOfWork.DiscardChanges();
            }
        }
    }

    /// <summary>
    /// The debt of each profile once these payments are cancelled and the others attached to it, read before the change; each profile is marked changed, so a parallel payment,
    /// cancellation or settlement that read the same debt cannot also be saved. Payments without a profile count in no debt.
    /// </summary>
    /// <param name="attached">The payments without a customer that this list attaches to a profile, with their amount: each lowers that debt.</param>
    private async Task<IReadOnlyList<(Customer Customer, decimal Debt)>> DebtsAfterAsync(
        IReadOnlyList<CustomerPayment> cancelled, IReadOnlyList<(Customer Customer, decimal Amount)> attached, CancellationToken cancellationToken)
    {
        // A payment that never counted (waiting for its transfer) leaves the debt as it is when it is cancelled.
        var changes = cancelled
            .Where(p => p.Customer is not null)
            .Select(p => (Customer: p.Customer!, Change: p.StatusId == StatusIds.CustomerPayment.PendingTransfer
                ? 0m
                : CustomerPaymentRules.DebtAfter(0m, p.Kind, p.Amount, cancelled: true, p.DiscountAmount)))
            .Concat(attached.Select(a => (Customer: a.Customer, Change: -a.Amount)))
            .GroupBy(c => c.Customer.Id)
            .ToList();
        var before = await debts.DebtsAsync(changes.Select(g => g.Key).ToList(), cancellationToken);
        var after = new List<(Customer Customer, decimal Debt)>(changes.Count);
        foreach (var group in changes)
        {
            var customer = group.First().Customer;
            unitOfWork.MarkChanged(customer);
            after.Add((customer, before[group.Key] + group.Sum(c => c.Change)));
        }

        return after;
    }

    /// <summary>MSG-GEN-09 naming the payment and its status (GBR-01).</summary>
    private AppError WrongStatus(CustomerPayment payment) => AppError.Conflict(
        UserMessages.WrongStatus($"Customer payment {payment.Code}", statusCatalog.DisplayNameOf(payment.StatusId)),
        new Dictionary<string, object?> { ["paymentId"] = payment.Id });
}
