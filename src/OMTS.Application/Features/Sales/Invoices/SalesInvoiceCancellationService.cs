using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Application.Features.Sales.Products;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-08 – the Owner (SALES_INVOICES Delete) cancels an invoice for a sale that did not happen (BR-SALE-11). A payment is cancelled only when the Owner answers that its cash was given back (MSG-SALE-23);
/// each lot this invoice closed is reopened, a walk-in invoice with kept payments first gets a profile (MSG-SALE-19), and the trader is told after the commit, whatever the service answers (GFR-06).
/// A linked request is no longer Fulfilled, and revenue, debt and yield no longer count the invoice, because those are worked out from its status. The parts of the advance of its order set against it are
/// taken off (the advance is the order's again, and is never cancelled with the invoice), and a Completed order is Delivering again.
/// </summary>
public sealed class SalesInvoiceCancellationService(
    ISalesInvoiceRepository invoices,
    SalesInvoiceChangeGuard guard,
    SalesInvoiceFactsReader factsReader,
    ISalesOrderRepository orders,
    SalesOrderProgress orderProgress,
    IHarvestLotRepository lots,
    HarvestLotStatusService lotStatuses,
    ProductStockLedger productLedger,
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
        if (await guard.RefusalAsync(invoice, request.RowVersion, PermissionAction.Delete, cancellationToken) is { } refused)
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
        var order = invoice.SalesOrderId is { } orderId ? await orders.GetForEditAsync(orderId, cancellationToken) : null;
        var advances = order is null || order.CustomerId is null ? [] : await orders.CountedAdvancesForUpdateAsync(order.Id, cancellationToken);
        var debtsAfter = await DebtAfterAsync(invoice, customer, givenBack.Value!, keptPayments, order, advances, cancellationToken);
        if (!request.ConfirmOwedBack && debtsAfter.FirstOrDefault(d => d.DebtAfter < 0m) is { } owed)
        {
            var amount = CustomerDebtMath.OwedBack(owed.DebtAfter);
            return AppError.Conflict(
                UserMessages.MoneyWillBeOwedBack("cancellation", owed.Customer.Name, amount),
                new Dictionary<string, object?> { ["confirmationField"] = "confirmOwedBack", ["customerName"] = owed.Customer.Name, ["owedBackAmount"] = amount });
        }

        await ApplyAsync(invoice, reason, givenBack.Value!, chosen.Value, debtsAfter, order, advances, cancellationToken);
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

    // What the customer owes apart from the advances held for open orders, once the invoice is cancelled. The parts of the advance of its order that covered the invoice are the advance's again, and the order
    // is open after the cancellation (Delivering): all that is left of the advance is held for it, so it is no longer free credit.
    private async Task<List<CustomerDebtAfterChange>> DebtAfterAsync(
        SalesInvoice invoice, Customer? customer, IReadOnlyList<CustomerPayment> givenBack, decimal keptPayments, SalesOrder? order, IReadOnlyList<CustomerPayment> advances,
        CancellationToken cancellationToken)
    {
        if (customer is null)
        {
            return [];
        }

        var before = (await debts.AllocationAsync(customer.Id, cancellationToken)).DebtApartFromReserved;
        var after = invoice.CustomerId is null
            ? SalesInvoiceChangeMoney.AfterWalkInCancelled(before, keptPayments)
            : SalesInvoiceChangeMoney.AfterCancellation(before, invoice.Total, givenBack.Sum(p => p.Amount)) + ReservedGain(invoice, order, advances);
        return [new CustomerDebtAfterChange(customer, after)];
    }

    private static decimal ReservedGain(SalesInvoice invoice, SalesOrder? order, IReadOnlyList<CustomerPayment> advances)
    {
        if (order is null)
        {
            return 0m;
        }

        var left = advances.Sum(SalesOrderViews.Left);
        var covering = advances.SelectMany(a => a.Allocations).Where(a => a.InvoiceId == invoice.Id).Sum(a => a.Amount);
        var reservedBefore = SalesOrderRules.IsOpen(order.StatusId) ? left : 0m;
        return left + covering - reservedBefore;
    }

    private async Task ApplyAsync(
        SalesInvoice invoice, string reason, IReadOnlyList<CustomerPayment> givenBack, Customer? chosen, IReadOnlyList<CustomerDebtAfterChange> debtsAfter, SalesOrder? order,
        IReadOnlyList<CustomerPayment> advances, CancellationToken cancellationToken)
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
        // The kg the invoice took leave no product stock any more: its Sale rows are undone in this save.
        await productLedger.ReverseAsync(invoice.Id, cancellationToken);

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

        var orderMoved = order is not null && ReleaseOrder(invoice, order, advances);
        audit.Record(new AuditEntry(CancelledAction, SalesInvoiceWriter.RecordType)
        {
            RecordId = invoice.Id,
            RecordCode = invoice.Code,
            ZoneId = invoice.ZoneId,
            Reason = reason,
            Before = before,
            After = new
            {
                Status = statusCatalog.CodeOf(invoice.StatusId), invoice.CustomerId, CashGivenBack = givenBack.Select(p => p.Code).ToList(), ReopenedLots = reopened, OrderReopened = orderMoved,
            },
        });
    }

    // The parts of the advance of the order set against this invoice are taken off (the advance is never cancelled with the invoice), and the order is marked changed, so a parallel invoice or finish of the
    // same order and this save cannot both win. A Completed order is Delivering again; the others keep their status. Returns whether the order was reopened.
    private bool ReleaseOrder(SalesInvoice invoice, SalesOrder order, IReadOnlyList<CustomerPayment> advances)
    {
        foreach (var allocation in advances.SelectMany(a => a.Allocations).Where(a => a.InvoiceId == invoice.Id).ToList())
        {
            unitOfWork.MarkChanged(allocation.Payment);
            invoices.RemoveAllocation(allocation);
        }

        unitOfWork.MarkChanged(order);
        return orderProgress.OnInvoiceCancelled(order);
    }
}
