using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// BR-DEBT-01: the unpaid invoices whose message (MSG-SALE-15) was accepted by the service with the transfer details of the current receiving account, so a transfer made
/// from it after a change is not recorded automatically. Unpaid = Completed with a part still to pay after its own payments that are not Cancelled (the oldest-first
/// allocation of the other payments, BR-DEBT-05, comes with PR 8a). A message names the account in its parameters only when it carried it.
/// </summary>
internal sealed class SentInvoiceQrCodeQuery(OmtsDbContext db) : ISentInvoiceQrCodeQuery
{
    public async Task<int> CountUnpaidInvoicesWithSentCodesAsync(CancellationToken cancellationToken)
    {
        var cooperative = await db.Cooperatives.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (cooperative?.BankAccount is not { } account)
        {
            return 0;
        }

        var accountParameter = Parameter(UserMessages.InvoiceMessageBankAccountParameter, account.AccountNumber);
        var bankParameter = Parameter(UserMessages.InvoiceMessageBankNameParameter, account.BankName);
        return await db.SalesInvoices.AsNoTracking()
            .Where(i => i.StatusId == StatusIds.SalesInvoice.Completed)
            .Where(i => i.Total > (db.CustomerPayments
                .Where(p => p.InvoiceId == i.Id && p.Kind == CustomerPaymentKind.Payment && p.StatusId != StatusIds.CustomerPayment.Cancelled)
                .Sum(p => (decimal?)p.Amount) ?? 0m))
            .Where(i => db.Notifications.Any(n =>
                n.RelatedRecordType == RelatedRecordTypes.SalesInvoice && n.RelatedRecordId == i.Id && n.Type == SalesInvoiceMessages.MessageType
                && n.DeliveryStatusId == StatusIds.MessageDelivery.Sent
                && n.MessageParams != null && n.MessageParams.Contains(accountParameter) && n.MessageParams.Contains(bankParameter)))
            .CountAsync(cancellationToken);
    }

    // As MessageParameterJson writes one string value of the object: "name":"value".
    private static string Parameter(string name, string value) =>
        MessageParameterJson.Serialize(new Dictionary<string, object?> { [name] = value })!.TrimStart('{').TrimEnd('}');
}
