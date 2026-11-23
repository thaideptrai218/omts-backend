using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models.Payments;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Sales.Checkout.Dtos;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Checkout;

/// <summary>
/// A trader pays one invoice still owed from the Zalo Mini App (decision of 21/11/2026). Zalo's Checkout SDK is required for a payment inside a Mini App and payOS is the
/// third party, as the payment sandbox did; the web keeps its own VietQR flow. Starting a checkout records a customer payment waiting for its transfer, of what is left to
/// pay on the invoice, into the bank account of the payOS channel, makes the payOS link of the same amount and signs the Checkout SDK order. The transfer is recorded
/// by the payOS notification (<see cref="PayOsNoticeService"/>), through the same rules as any transfer (<see cref="BankTransferNoticeService"/>). Only the trader's own
/// invoices and checkouts: another one answers as not found. The client never decides that the money arrived (CLAUDE.md §10).
/// </summary>
public sealed class TraderCheckoutService(
    ITraderCheckoutRepository checkouts,
    ISalesInvoiceRepository invoices,
    ICustomerRepository customers,
    ICooperativeRepository cooperatives,
    ICashAccountRepository cashAccounts,
    CustomerDebtReader debts,
    CustomerPaymentService payments,
    CustomerPaymentStatusService paymentStatuses,
    IPayOsGateway payOs,
    IZaloCheckout zalo,
    TraderCheckoutReporter reporter,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<TraderCheckoutService> logger)
{
    public const string ObjectName = "Checkout";

    private const int OrderCodeTries = 5;

    /// <summary>
    /// A checkout of what is left to pay on the invoice. A checkout of the invoice still being paid with the same amount is handed out again; one that expired or asks
    /// another amount is cancelled (its payment never counted) and a new one starts.
    /// </summary>
    public async Task<Result<TraderCheckoutStarted>> StartAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        if (!payOs.IsConfigured || !zalo.IsConfigured)
        {
            return Unavailable();
        }

        if (await ProfileAsync(cancellationToken) is not { } customer)
        {
            return AppError.Invalid("Customer profile");
        }

        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null || invoice.CustomerId != customer.Id)
        {
            return AppError.NotFound;
        }

        var allocation = await debts.AllocationAsync(customer.Id, cancellationToken);
        var state = SalesInvoicePaidState.Of(invoice.Id, invoice.StatusId, invoice.Total, customer.Id, ownPayments: 0m, allocation);
        if (state.IsPaid is not false)
        {
            var status = state.IsPaid is null ? statusCatalog.DisplayNameOf(invoice.StatusId) : "Paid";
            return AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", status), new Dictionary<string, object?> { ["invoiceId"] = invoice.Id });
        }

        var amount = state.Unpaid;
        var now = timeProvider.GetUtcNow();
        var me = currentUser.AccountId!.Value;
        if (await checkouts.FindPendingOfInvoiceAsync(invoice.Id, cancellationToken) is { } pending)
        {
            if (pending.AccountId == me && TraderCheckoutRules.Reusable(pending, amount, now))
            {
                return new TraderCheckoutStarted(false, View(pending, invoice.Code, now));
            }

            var cancelled = await paymentStatuses.CancelAsync(pending.PaymentId, new CancelCustomerPaymentRequest(TraderCheckoutRules.ReplacedReason), cancellationToken);
            if (!cancelled.IsSuccess)
            {
                return cancelled.Error!;
            }

            unitOfWork.DiscardChanges();
        }

        if (await PaidIntoAsync(cancellationToken) is not { } cashAccount)
        {
            logger.LogWarning("A trader checkout cannot start: the cash book has no Bank account for the payOS channel (Checkout:PayOsAccountNumber, else the receiving bank account).");
            return AppError.Invalid("Cash account", "The cash book has no bank account for the payOS channel.");
        }

        var staged = await payments.StagePendingTransferAsync(
            new RecordCustomerPaymentRequest(
                customer.Id, PaymentDate: null, Amount: amount, Method: null, Note: TraderCheckoutRules.PaymentNote, CashAccountId: cashAccount.Id,
                Allocations: [new CustomerPaymentAllocationInput(invoice.Id, amount)]),
            cancellationToken);
        if (!staged.IsSuccess)
        {
            unitOfWork.DiscardChanges();
            return staged.Error!;
        }

        var payment = staged.Value!;
        var orderCode = await NewOrderCodeAsync(cancellationToken);
        var expiresAt = now + TraderCheckoutRules.Lifetime;
        var link = await payOs.CreateLinkAsync(new PayOsLinkRequest(orderCode, (long)amount, payment.Code, expiresAt), cancellationToken);
        if (link is null)
        {
            unitOfWork.DiscardChanges();
            return Unavailable();
        }

        if (!string.Equals(Plain(link.AccountNumber), Plain(cashAccount.AccountNumber), StringComparison.OrdinalIgnoreCase))
        {
            // payOS pays into the account linked to its channel: the payment is recorded against the cash book account chosen above, so they should be the same.
            logger.LogWarning("The payOS link pays into another account than the cash book account of the checkouts; payOS order {OrderCode}.", orderCode);
        }

        var checkout = new TraderCheckout
        {
            PaymentId = payment.Id,
            Payment = payment,
            InvoiceId = invoice.Id,
            AccountId = me,
            Amount = amount,
            PayOsOrderCode = orderCode,
            PayOsPaymentLinkId = link.PaymentLinkId,
            QrContent = link.QrContent,
            BankBin = link.BankBin,
            AccountNumber = link.AccountNumber,
            AccountName = link.AccountName,
            TransferContent = link.Description,
            ExpiresAt = expiresAt,
        };
        checkouts.Add(checkout);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new TraderCheckoutStarted(true, View(checkout, invoice.Code, now));
    }

    /// <summary>The Zalo order of <c>PAY_BY_CUSTOM_METHOD</c>: Zalo is told about it once the money arrives. Another order replaces it until Zalo accepted a report.</summary>
    public async Task<Result<TraderCheckoutView>> LinkZaloOrderAsync(Guid checkoutId, LinkZaloOrderRequest request, CancellationToken cancellationToken)
    {
        var orderId = request.ZaloOrderId?.Trim();
        if (string.IsNullOrEmpty(orderId) || orderId.Length > TraderCheckoutRules.MaxZaloOrderIdLength)
        {
            return AppError.Invalid("Zalo order id");
        }

        if (await MineForEditAsync(checkoutId, cancellationToken) is not { } checkout)
        {
            return AppError.NotFound;
        }

        if (checkout.ZaloOrderId != orderId)
        {
            if (checkout.ZaloReportedAt is not null)
            {
                return AppError.Conflict(UserMessages.WrongStatus($"{ObjectName} {checkout.Payment.Code}", "Reported to Zalo"));
            }

            checkout.ZaloOrderId = orderId;
            checkout.ZaloOrderLinkedAt = timeProvider.GetUtcNow();
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        await reporter.ReportAsync(checkout, ignoreGap: true, cancellationToken);
        return View(checkout, checkout.Invoice.Code, timeProvider.GetUtcNow());
    }

    /// <summary>The checkout as the QR screen polls it; a paid order Zalo has not accepted yet is reported again on the way.</summary>
    public async Task<Result<TraderCheckoutView>> GetAsync(Guid checkoutId, CancellationToken cancellationToken)
    {
        if (await MineForEditAsync(checkoutId, cancellationToken) is not { } checkout)
        {
            return AppError.NotFound;
        }

        await reporter.ReportAsync(checkout, ignoreGap: false, cancellationToken);
        return View(checkout, checkout.Invoice.Code, timeProvider.GetUtcNow());
    }

    private TraderCheckoutView View(TraderCheckout checkout, string invoiceCode, DateTimeOffset now)
    {
        var payment = checkout.Payment;
        var completed = payment.StatusId == StatusIds.CustomerPayment.Completed;
        var waiting = payment.StatusId == StatusIds.CustomerPayment.PendingTransfer;
        var order = zalo.Sign(
            (long)checkout.Amount, TraderCheckoutRules.Description(invoiceCode), [new ZaloCheckoutItem(invoiceCode, (long)checkout.Amount)],
            TraderCheckoutRules.Extradata(checkout.Id));
        return new TraderCheckoutView(
            checkout.Id, checkout.InvoiceId, invoiceCode, payment.Code, checkout.Amount, payment.StatusId, statusCatalog.CodeOf(payment.StatusId),
            statusCatalog.DisplayNameOf(payment.StatusId), completed ? payment.Amount : null, TraderCheckoutRules.PaidInFull(checkout), waiting && checkout.ExpiresAt <= now,
            checkout.ExpiresAt,
            new TraderCheckoutQr(checkout.QrContent, checkout.BankBin, checkout.AccountNumber, checkout.AccountName, checkout.Amount, checkout.TransferContent),
            order, checkout.ZaloOrderId is not null, checkout.ZaloReportedAt is not null);
    }

    private async Task<TraderCheckout?> MineForEditAsync(Guid checkoutId, CancellationToken cancellationToken)
    {
        var checkout = await checkouts.GetForEditAsync(checkoutId, cancellationToken);
        return checkout is not null && checkout.AccountId == currentUser.AccountId ? checkout : null;
    }

    /// <summary>The profile linked to the trader's account; a merged profile holds nothing any more.</summary>
    private async Task<Customer?> ProfileAsync(CancellationToken cancellationToken)
    {
        var customer = await customers.GetByAccountIdAsync(currentUser.AccountId!.Value, cancellationToken);
        return customer is null || customer.MergedIntoCustomerId is not null ? null : customer;
    }

    /// <summary>
    /// The cash book Bank account the payments are recorded into: the one of the account linked to the payOS channel when it is set, else the cooperative's receiving
    /// bank account (DEBT-01). Null when the cash book has none.
    /// </summary>
    private async Task<CashAccount?> PaidIntoAsync(CancellationToken cancellationToken)
    {
        if (payOs.LinkedAccount is { } linked)
        {
            return await cashAccounts.FindBankAccountAsync(linked.BankBin, linked.AccountNumber, cancellationToken);
        }

        return (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount is { } receiving
            ? await cashAccounts.FindBankAccountAsync(receiving.BankBin, receiving.AccountNumber, cancellationToken)
            : null;
    }

    /// <summary>A payOS order code no checkout has; the unique index refuses the rare one taken between the check and the save.</summary>
    private async Task<long> NewOrderCodeAsync(CancellationToken cancellationToken)
    {
        var code = TraderCheckoutRules.NewOrderCode();
        for (var attempt = 1; attempt < OrderCodeTries && await checkouts.OrderCodeTakenAsync(code, cancellationToken); attempt++)
        {
            code = TraderCheckoutRules.NewOrderCode();
        }

        return code;
    }

    private static string Plain(string accountNumber) => string.Concat(accountNumber.Where(c => !char.IsWhiteSpace(c)));

    private static AppError Unavailable() => new(ErrorKind.ServiceUnavailable, UserMessages.ServiceNotResponding("payOS"));
}
