using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>A sale awaiting its transfer as the port sees it, with its VietQR code made again for the receiving bank account set now (BR-DEBT-01, BR-SALE-07 (7)).</summary>
public sealed class PendingSaleViews(
    ICooperativeRepository cooperatives,
    ICustomerPaymentRepository payments,
    ISalesInvoiceRepository invoices,
    IAccountRepository accounts,
    SalesInvoiceViews invoiceViews,
    ZoneAccessGuard zoneAccess)
{
    /// <summary>The Zone Managers and Owners who sell in the zone of the sale see it, change its payment or stop it (SALES_INVOICES Create, BR-SALE-08).</summary>
    public async Task<bool> MayUseAsync(PendingSale sale, CancellationToken cancellationToken) =>
        (await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, cancellationToken)).Allows(sale.ZoneId);

    /// <summary>The VietQR code of an amount for the receiving bank account, or null when the cooperative has none (MSG-SALE-13).</summary>
    public static VietQrView? VietQr(ReceivingBankAccount? account, decimal amount, string invoiceCode) => account is null
        ? null
        : new VietQrView(
            VietQrPayload.Build(account.BankBin, account.AccountNumber, (long)amount, invoiceCode),
            account.BankBin, account.BankName, account.AccountNumber, account.AccountHolderName, amount, VietQrPayload.ContentOf(invoiceCode));

    public async Task<PendingSaleView> ViewAsync(PendingSale sale, CancellationToken cancellationToken) =>
        (await ViewAsync([sale], cancellationToken))[0];

    public async Task<IReadOnlyList<PendingSaleView>> ViewAsync(IReadOnlyList<PendingSale> sales, CancellationToken cancellationToken)
    {
        if (sales.Count == 0)
        {
            return [];
        }

        var account = sales.Any(s => s.Status == PendingSaleStatus.Awaiting) ? (await cooperatives.GetCurrentAsync(cancellationToken))?.BankAccount : null;
        var names = await accounts.GetDisplayNamesAsync(sales.Select(s => s.CreatedByAccountId).Distinct().ToList(), cancellationToken);
        // One query for the money received of every sale still awaiting, whatever their number.
        var awaitingCodes = sales.Where(s => s.Status == PendingSaleStatus.Awaiting).Select(s => s.Code).ToList();
        IReadOnlyDictionary<string, decimal> receivedByCode = awaitingCodes.Count == 0 ? new Dictionary<string, decimal>() : await payments.ReceivedWithoutInvoiceAsync(awaitingCodes, cancellationToken);
        var views = new List<PendingSaleView>(sales.Count);
        foreach (var sale in sales)
        {
            var awaiting = sale.Status == PendingSaleStatus.Awaiting;
            var received = awaiting ? receivedByCode.GetValueOrDefault(sale.Code) : 0m;
            SalesInvoiceSaved? saved = null;
            if (sale.InvoiceId is { } invoiceId && await invoices.GetAsync(invoiceId, cancellationToken) is { } invoice)
            {
                saved = await invoiceViews.SavedAsync(invoice, change: 0m, savedOnTransfer: sale.Status == PendingSaleStatus.Completed, cancellationToken);
            }

            views.Add(new PendingSaleView(
                sale.Code,
                sale.Status,
                new SalesInvoiceZoneView(sale.Zone.Id, sale.Zone.Code, sale.Zone.Name),
                sale.Customer is { } c ? new SalesInvoiceCustomerView(c.Id, c.Name, c.PhoneNumber) : null,
                sale.CustomerId is null,
                sale.Total,
                sale.CashAmount,
                sale.ToPayAmount,
                received,
                awaiting ? VietQr(account, sale.ToPayAmount, sale.Code) : null,
                sale.CreatedAt,
                new AccountRef(sale.CreatedByAccountId, names.GetValueOrDefault(sale.CreatedByAccountId) ?? string.Empty),
                sale.InvoiceId,
                saved,
                sale.EndedAt));
        }

        return views;
    }
}
