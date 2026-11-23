using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Checkout.Dtos;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Sales.Checkout;

/// <summary>
/// payOS reports the transfer of a trader's Mini App checkout. The money is "paid" only when the four conditions of CLAUDE.md §10 hold: (1) the signature of the checksum
/// key is right (checked by the gateway, the webhook answers 401 otherwise); (2) the order code is that of a checkout, and its payment waits for the transfer; (3) the amount:
/// the payment takes what arrived, so a short transfer is a part payment and Zalo is told only of a payment in full; (4) the bank reference was never recorded, so a
/// notification sent again records nothing. The transfer goes through the same rules as a bank notification (<see cref="BankTransferNoticeService"/>): the content carries
/// the payment code, which completes the payment waiting for it, and the invoice code, which records money that arrives after the payment was cancelled as a payment of the
/// invoice, so no money that arrived is lost.
/// </summary>
public sealed class PayOsNoticeService(
    IPayOsGateway payOs,
    ITraderCheckoutRepository checkouts,
    ICooperativeRepository cooperatives,
    BankTransferNoticeService notices,
    TraderCheckoutReporter reporter,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<PayOsNoticeService> logger)
{
    /// <summary>Null when the notification is not authentic (the webhook answers 401 and reads nothing of it).</summary>
    public async Task<PayOsNoticeResult?> ReceiveAsync(byte[] body, CancellationToken cancellationToken)
    {
        var notification = payOs.Read(body);
        if (!notification.Authentic || notification.Transfer is not { } transfer)
        {
            return null;
        }

        if (!transfer.Successful)
        {
            return PayOsNoticeResult.Skipped("not-successful");
        }

        if (await checkouts.FindByOrderCodeAsync(transfer.OrderCode, cancellationToken) is not { } checkout)
        {
            // The test notification payOS sends when the webhook is registered names no checkout; money of an unknown order is not ours to record.
            logger.LogInformation("payOS notification for unknown order {OrderCode}: nothing recorded.", transfer.OrderCode);
            return PayOsNoticeResult.Skipped("unknown-order");
        }

        // A payment still waiting is found by its own bank account; any other transfer is recorded on the receiving bank account, the one a bank notification checks.
        var waiting = checkout.Payment.StatusId == StatusIds.CustomerPayment.PendingTransfer;
        var account = waiting ? checkout.Payment.CashAccount?.AccountNumber : (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount?.AccountNumber;
        if (string.IsNullOrWhiteSpace(account))
        {
            logger.LogWarning("payOS notification for checkout {CheckoutId}: the cooperative has no receiving bank account, nothing recorded.", checkout.Id);
            return PayOsNoticeResult.Skipped("no-account");
        }

        var reference = string.IsNullOrWhiteSpace(transfer.Reference) ? $"PAYOS-{transfer.OrderCode}" : transfer.Reference.Trim();
        var notice = new BankTransferNotice(
            reference, account, transfer.Amount, $"{checkout.Payment.Code} {checkout.Invoice.Code}", transfer.OccurredAt ?? timeProvider.GetUtcNow(), BankTransferDirection.In);
        var checkoutId = checkout.Id;
        var result = (await notices.ReceiveAsync([notice], cancellationToken)).Transactions.Single();
        logger.LogInformation("payOS notification for checkout {CheckoutId}: {Outcome} {PaymentCode}.", checkoutId, result.Outcome, result.PaymentCode);

        unitOfWork.DiscardChanges();
        if (await checkouts.GetForEditAsync(checkoutId, cancellationToken) is { } recorded)
        {
            await reporter.ReportAsync(recorded, ignoreGap: true, cancellationToken);
        }

        return new PayOsNoticeResult(result.Outcome, result.PaymentCode, result.Reason?.ToString());
    }
}
