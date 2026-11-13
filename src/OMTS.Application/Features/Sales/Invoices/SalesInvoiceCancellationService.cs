using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-08 – the Cooperative Owner cancels an invoice for a sale that did not happen (BR-SALE-11). A payment is cancelled only when the Owner answers that its cash was given back (MSG-SALE-23);
/// each lot this invoice closed is reopened, a walk-in invoice with kept payments first gets a profile (MSG-SALE-19), and the trader is told after the commit, whatever the service answers (GFR-06).
/// A linked request is no longer Fulfilled, and revenue, debt and yield no longer count the invoice, because those are worked out from its status.
/// </summary>
public sealed class SalesInvoiceCancellationService(
    ISalesInvoiceRepository invoices,
    SalesInvoiceChangeGuard guard,
    SalesInvoiceFactsReader factsReader,
    IHarvestLotRepository lots,
    HarvestLotStatusService lotStatuses,
    CustomerPaymentStatusChanges paymentChanges,
    CustomerDebtReader debts,
    SalesInvoiceOwedBackNotifier owedBack,
    SalesInvoiceMessages messages,
    SalesInvoiceChangeSending sending,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string CancelledAction = "SalesInvoice.Cancelled";

    public async Task<Result<SalesInvoiceSent>> CancelAsync(Guid invoiceId, CancelSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetForSendingAsync(invoiceId, cancellationToken);
        if (await guard.RefusalAsync(invoice, request.RowVersion, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (CustomerPaymentRules.Reason(request.Reason) is not { } reason)
        {
            return AppError.Invalid("Reason");
        }

        var givenBack = AnsweredCash(invoice!, request.CashGivenBack);
        if (!givenBack.IsSuccess)
        {
            return givenBack.Error!;
        }

        var keptPayments = SalesInvoiceChangeMoney.PaidOf(invoice!.Payments) - givenBack.Value!.Sum(p => p.Amount);
        var chosen = await ChosenCustomerAsync(invoice, request.CustomerId, keptPayments, cancellationToken);
        if (!chosen.IsSuccess)
        {
            return chosen.Error!;
        }

        var customer = chosen.Value ?? invoice.Customer;
        var debtsAfter = await DebtAfterAsync(invoice, customer, givenBack.Value!, keptPayments, cancellationToken);
        if (!request.ConfirmOwedBack && debtsAfter.FirstOrDefault(d => d.DebtAfter < 0m) is { } owed)
        {
            var amount = CustomerDebtMath.OwedBack(owed.DebtAfter);
            return AppError.Conflict(
                UserMessages.MoneyWillBeOwedBack("cancellation", owed.Customer.Name, amount),
                new Dictionary<string, object?> { ["confirmationField"] = "confirmOwedBack", ["customerName"] = owed.Customer.Name, ["owedBackAmount"] = amount });
        }

        await ApplyAsync(invoice, reason, givenBack.Value!, chosen.Value, debtsAfter, cancellationToken);
        await owedBack.NotifyAsync(invoice, debtsAfter, "cancelled", cancellationToken);
        var notice = await messages.QueueCancellationAsync(invoice, keptPayments, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await sending.AnswerCancelledAsync(invoice.Id, notice);
    }

    /// <summary>SALE-08.3: every cash payment Awaiting confirmation is answered; a Yes returns it. An answer for another payment is a mistake (MSG-GEN-01).</summary>
    private static Result<IReadOnlyList<CustomerPayment>> AnsweredCash(SalesInvoice invoice, IReadOnlyList<CashGivenBackAnswer>? answers)
    {
        if (answers?.Any(a => a is null) == true)
        {
            return AppError.Invalid("Cash given back");
        }

        var awaiting = invoice.Payments.Where(p => p.Method == CustomerPaymentMethod.Cash && p.Kind == CustomerPaymentKind.Payment && p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation)
            .OrderBy(p => p.Code, StringComparer.Ordinal)
            .ToList();
        var given = (answers ?? []).GroupBy(a => a.PaymentId).ToDictionary(g => g.Key, g => g.Last().GivenBack);
        if (given.Keys.Except(awaiting.Select(p => p.Id)).Any())
        {
            return AppError.Invalid("Cash given back");
        }

        var unanswered = awaiting.FirstOrDefault(p => !given.ContainsKey(p.Id));
        if (unanswered is not null)
        {
            return AppError.Conflict(
                UserMessages.CashGivenBackQuestion(unanswered.Amount, unanswered.PaymentDate, invoice.Customer?.Name ?? "the buyer"),
                new Dictionary<string, object?> { ["confirmationField"] = "cashGivenBack", ["paymentId"] = unanswered.Id, ["amount"] = unanswered.Amount, ["paymentDate"] = unanswered.PaymentDate });
        }

        return awaiting.Where(p => given[p.Id]).ToList();
    }

    // SALE-08.2: a walk-in invoice with payments kept gets a profile; a profile is only taken in that case.
    private async Task<Result<Customer?>> ChosenCustomerAsync(SalesInvoice invoice, Guid? customerId, decimal keptPayments, CancellationToken cancellationToken)
    {
        if (invoice.CustomerId is null && keptPayments > 0m)
        {
            if (customerId is null)
            {
                return AppError.Conflict(
                    UserMessages.WalkInBuyerProfileNeeded(invoice.Code, keptPayments),
                    new Dictionary<string, object?> { ["confirmationField"] = "customerId", ["owedBackAmount"] = keptPayments });
            }

            return await factsReader.ReadCustomerAsync(customerId, cancellationToken);
        }

        return customerId is null ? Result<Customer?>.Success(null) : AppError.Invalid("Customer");
    }

    private async Task<List<CustomerDebtAfterChange>> DebtAfterAsync(
        SalesInvoice invoice, Customer? customer, IReadOnlyList<CustomerPayment> givenBack, decimal keptPayments, CancellationToken cancellationToken)
    {
        if (customer is null)
        {
            return [];
        }

        var before = await debts.DebtAsync(customer.Id, cancellationToken);
        var after = invoice.CustomerId is null
            ? SalesInvoiceChangeMoney.AfterWalkInCancelled(before, keptPayments)
            : SalesInvoiceChangeMoney.AfterCancellation(before, invoice.Total, givenBack.Sum(p => p.Amount));
        return [new CustomerDebtAfterChange(customer, after)];
    }

    private async Task ApplyAsync(
        SalesInvoice invoice, string reason, IReadOnlyList<CustomerPayment> givenBack, Customer? chosen, IReadOnlyList<CustomerDebtAfterChange> debtsAfter, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = BusinessClock.ToBusinessDate(now);
        var before = new { Status = statusCatalog.CodeOf(invoice.StatusId), invoice.CustomerId, invoice.Total };
        foreach (var payment in givenBack)
        {
            paymentChanges.Cancel(payment, reason);
        }

        if (chosen is not null)
        {
            invoice.CustomerId = chosen.Id;
            invoice.Customer = chosen;
            invoice.Payments.ToList().ForEach(p => p.CustomerId = chosen.Id);
        }

        foreach (var profile in debtsAfter)
        {
            unitOfWork.MarkChanged(profile.Customer);
        }

        unitOfWork.MarkChanged(invoice);
        invoice.StatusId = StatusIds.SalesInvoice.Cancelled;
        invoice.CancelledAt = now;
        invoice.CancelledByAccountId = currentUser.AccountId;
        invoice.CancelReason = reason;

        var lotIds = invoice.Lines.Select(l => l.HarvestLotId).Distinct().ToList();
        var reopened = new List<string>();
        foreach (var lot in await lots.ListForInvoiceAsync(lotIds, cancellationToken))
        {
            // Only the lots this invoice closed (BR-HARVEST-04): Reopen refuses every other one.
            if (lotStatuses.Reopen(lot, today, HarvestLotReopenCause.SalesInvoiceCancelled, invoice.Id))
            {
                reopened.Add(lot.LotCode);
            }
        }

        audit.Record(new AuditEntry(CancelledAction, SalesInvoiceWriter.RecordType)
        {
            RecordId = invoice.Id,
            RecordCode = invoice.Code,
            ZoneId = invoice.ZoneId,
            Reason = reason,
            Before = before,
            After = new { Status = statusCatalog.CodeOf(invoice.StatusId), invoice.CustomerId, CashGivenBack = givenBack.Select(p => p.Code).ToList(), ReopenedLots = reopened },
        });
    }
}
