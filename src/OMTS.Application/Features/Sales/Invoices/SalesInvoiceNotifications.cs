using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>SALE-05 step 12: MSG-SALE-08 to the Cooperative Owners, saved with the invoice and pushed after it commits (GFR-06). The Owner is told of the Owner's own sales too (BR-SALE-03).</summary>
public sealed class SalesInvoiceNotifications(IZoneRecipientQuery recipients, IAccountRepository accounts, INotificationDispatcher notifications)
{
    public const string Title = "New sale";
    public const string WalkInCustomerName = "a walk-in customer";

    public static string PaymentTermText(SalesPaymentTerm term) => term == SalesPaymentTerm.PayLater ? "Pay later" : "Pay now";

    internal async Task SaleAsync(SalesInvoice invoice, SalesInvoiceDecision decision, Guid sellerAccountId, CancellationToken cancellationToken)
    {
        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        var names = await accounts.GetDisplayNamesAsync([sellerAccountId], cancellationToken);
        var decidedPrices = decision.Lines
            .Where(l => l.PriceSource == SalesPriceSource.DecidedByOwner)
            .Select(l => (l.UnitPrice, l.ListPrice))
            .ToList();
        var message = UserMessages.SaleNotice(
            names.GetValueOrDefault(sellerAccountId) ?? string.Empty,
            decision.Lines.Sum(l => l.Entry.Kg),
            invoice.Customer?.Name ?? WalkInCustomerName,
            invoice.Total,
            invoice.Code,
            PaymentTermText(invoice.PaymentTerm),
            UserMessages.SalePriceText(decidedPrices, invoice.DiscountAmount));
        await notifications.NotifyAsync(
            owners,
            new NotificationContent(NotificationTypes.SaleCompleted, Title, message, new RelatedRecord(RelatedRecordTypes.SalesInvoice, invoice.Id, invoice.Code)),
            cancellationToken);
    }
}
