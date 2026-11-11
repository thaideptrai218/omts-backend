using Microsoft.Extensions.Options;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The invoice as the API shows it, from <see cref="ISalesInvoiceRepository.GetAsync"/> (zone, customer, lines, price cuts and payments loaded), with its sendings (SALE-06 step 5), what it
/// does to the customer debt and the commands offered to the caller (SALE-09.1 steps 2-3).
/// </summary>
public sealed class SalesInvoiceViews(
    IAccountRepository accounts,
    ISalesInvoiceRepository invoices,
    CustomerDebtReader debts,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    IOptions<ProvenanceOptions> provenance,
    TimeProvider timeProvider)
{
    /// <summary>
    /// The answer of a save, read back as saved: MSG-SALE-06 names the transfer received with the VietQR code, the cash recorded and the part added to the debt. Only the cash of the
    /// port names an invoice (BR-SALE-07 (3)); of the transfers, only the one that completed the sale counts, not a later one for the same invoice.
    /// </summary>
    /// <param name="savedOnTransfer">The bank notification saved the invoice (GFR-08): its first VietQR payment, the lowest code, is the transfer of the sale.</param>
    public async Task<SalesInvoiceSaved> SavedAsync(SalesInvoice invoice, decimal change, bool savedOnTransfer, CancellationToken cancellationToken)
    {
        var payments = invoice.Payments.Where(p => p.Kind == CustomerPaymentKind.Payment).ToList();
        var cash = payments.Where(p => p.Method == CustomerPaymentMethod.Cash).Sum(p => p.Amount);
        var vietQr = savedOnTransfer
            ? payments.Where(p => p.Method == CustomerPaymentMethod.VietQrInvoice).OrderBy(p => p.Code, StringComparer.Ordinal).Select(p => p.Amount).FirstOrDefault()
            : 0m;
        var subtracted = Math.Max(0m, cash - invoice.Total);
        var later = invoice.PaymentTerm == SalesPaymentTerm.PayLater ? invoice.Total - (cash - subtracted) : 0m;
        var message = UserMessages.SaleCompleted(invoice.Code, cash, later, invoice.Customer?.Name ?? SalesInvoiceNotifications.WalkInCustomerName, vietQr);
        return new SalesInvoiceSaved(await ViewAsync(invoice, cancellationToken), change, subtracted, message.Text, message.Code);
    }

    public async Task<SalesInvoiceView> ViewAsync(SalesInvoice invoice, CancellationToken cancellationToken)
    {
        var people = invoice.Payments.Select(p => p.CollectedByAccountId).OfType<Guid>().Append(invoice.CreatedByAccountId).Distinct().ToList();
        var names = await accounts.GetDisplayNamesAsync(people, cancellationToken);
        var lines = invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new SalesInvoiceLineView(
            l.Id, l.LineNumber, l.ProductId, l.Product.Name, l.HarvestLotId, l.HarvestLot.LotCode, l.Kg, l.UnitPrice, l.ListPrice, l.PriceSource, l.Amount, l.SoldWholeLot,
            CageKgsOf(invoice, l.HarvestLotId))).ToList();
        var payments = invoice.Payments.OrderBy(p => p.CreatedAt).ThenBy(p => p.Code).Select(p => new CustomerPaymentView(
            p.Id, p.Code, p.Kind, p.Method, p.Amount, p.PaymentDate, p.CreatedAt, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId),
            p.CollectedByAccountId is { } collector ? Person(collector, names) : null)).ToList();
        var sendings = await SendingsAsync(invoice.Id, cancellationToken);
        var latest = LatestStatus(sendings);
        var debtEffect = await DebtEffectAsync(invoice, cancellationToken);
        var allowedActions = SalesInvoiceAllowedActions.Of(
            // CLAIM-03 seam: a sales return that is not cancelled goes in here when returns exist; none can exist yet.
            new SalesInvoiceActionFacts(invoice.StatusId, latest, SaleFinished: invoice.QrDecidedAt is not null, HasSalesReturn: false),
            await RightsAsync(invoice.ZoneId, cancellationToken));
        return new SalesInvoiceView(
            invoice.Id,
            invoice.Code,
            invoice.InvoicedAt,
            invoice.InvoiceDate,
            new SalesInvoiceZoneView(invoice.Zone.Id, invoice.Zone.Code, invoice.Zone.Name),
            invoice.Customer is { } c ? new SalesInvoiceCustomerView(c.Id, c.Name, c.PhoneNumber) : null,
            invoice.CustomerId is null,
            invoice.PurchaseRequestId,
            invoice.TakesRequestInFull,
            lines,
            lines.Sum(l => l.Kg),
            invoice.Subtotal,
            invoice.DiscountAmount,
            invoice.DiscountDecidedByOwner,
            invoice.OtherCharges,
            invoice.PriceCutTotal,
            invoice.Total,
            invoice.PaymentTerm,
            invoice.StatusId,
            statusCatalog.DisplayNameOf(invoice.StatusId),
            payments,
            Person(invoice.CreatedByAccountId, names),
            invoice.QrToken,
            QrUrlOf(invoice),
            invoice.QrDecidedAt,
            latest,
            statusCatalog.DisplayNameOf(latest),
            sendings,
            RowVersionGuard.Encode(invoice.RowVersion),
            invoice.PriceCuts.OrderBy(c => c.CutDate).ThenBy(c => c.Id).Select(c => new SalesInvoicePriceCutView(c.Amount, c.CutDate, c.Reason)).ToList(),
            debtEffect,
            allowedActions,
            invoice.PortWeighingId);
    }

    /// <summary>The kg of each cage of the lot in the weighing of the invoice, in the order weighed; empty for a sale without a weighing.</summary>
    public static IReadOnlyList<decimal> CageKgsOf(SalesInvoice invoice, Guid lotId) =>
        invoice.PortWeighing is { } weighing ? weighing.Cages.Where(c => c.HarvestLotId == lotId).OrderBy(c => c.CageNumber).Select(c => c.Kg).ToList() : [];

    /// <summary>The paid state of the invoice (BR-DEBT-05) and the debt of its profile now (BR-DEBT-02).</summary>
    private async Task<SalesInvoiceDebtEffect> DebtEffectAsync(SalesInvoice invoice, CancellationToken cancellationToken)
    {
        var allocation = invoice.CustomerId is { } customerId ? await debts.AllocationAsync(customerId, cancellationToken) : null;
        var ownPayments = invoice.Payments.Where(p => p.Kind == CustomerPaymentKind.Payment && p.StatusId != StatusIds.CustomerPayment.Cancelled).Sum(p => p.Amount);
        var state = SalesInvoicePaidState.Of(invoice.Id, invoice.StatusId, invoice.Total, invoice.CustomerId, ownPayments, allocation);
        return new SalesInvoiceDebtEffect(
            state.IsPaid, state.Covered, state.Unpaid, invoice.CustomerId is not null && invoice.StatusId != StatusIds.SalesInvoice.Cancelled, allocation?.Debt);
    }

    private async Task<SalesInvoiceCallerRights> RightsAsync(Guid zoneId, CancellationToken cancellationToken) =>
        new(await zoneAccess.IsCooperativeOwnerAsync(cancellationToken),
            await zoneAccess.AllowsAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, zoneId, cancellationToken),
            await zoneAccess.AllowsAsync(PermissionFunctions.SalesReturns, PermissionAction.Create, zoneId, cancellationToken),
            await zoneAccess.AllowsAsync(PermissionFunctions.SalesInvoices, PermissionAction.Export, zoneId, cancellationToken));

    /// <summary>The link of the QR code of the invoice, or null when it has none.</summary>
    public string? QrUrlOf(SalesInvoice invoice) => invoice.QrToken is { } token ? provenance.Value.LinkOf(token) : null;

    /// <summary>The sendings as shown, oldest first: a stale Sending row reads Not sent (<see cref="TraderMessageService.EffectiveStatus"/>).</summary>
    public async Task<IReadOnlyList<SalesInvoiceSendingView>> SendingsAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return (await invoices.ListSendingsAsync(invoiceId, cancellationToken))
            .Select(s =>
            {
                var status = TraderMessageService.EffectiveStatus(s.DeliveryStatusId, s.DeliveryStatusAt, now);
                return new SalesInvoiceSendingView(s.Channel.ToString(), s.PhoneNumber, status, statusCatalog.DisplayNameOf(status), s.DeliveryStatusAt);
            })
            .ToList();
    }

    /// <summary>BR-SALE-09: the status of the latest sending, Not sent when there is none.</summary>
    public static int LatestStatus(IReadOnlyList<SalesInvoiceSendingView> sendings) =>
        sendings.Count == 0 ? StatusIds.MessageDelivery.NotSent : sendings[^1].DeliveryStatusId;

    private static AccountRef Person(Guid id, IReadOnlyDictionary<Guid, string> names) => new(id, names.GetValueOrDefault(id) ?? string.Empty);
}
