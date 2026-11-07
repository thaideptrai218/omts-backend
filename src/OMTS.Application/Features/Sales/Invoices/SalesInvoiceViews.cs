using Microsoft.Extensions.Options;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The invoice as the API shows it, from <see cref="ISalesInvoiceRepository.GetAsync"/> (zone, customer, lines and payments loaded), with its sendings (SALE-06 step 5).</summary>
public sealed class SalesInvoiceViews(
    IAccountRepository accounts,
    ISalesInvoiceRepository invoices,
    IStatusCatalog statusCatalog,
    IOptions<ProvenanceOptions> provenance,
    TimeProvider timeProvider)
{
    public async Task<SalesInvoiceView> ViewAsync(SalesInvoice invoice, CancellationToken cancellationToken)
    {
        var people = invoice.Payments.Select(p => p.CollectedByAccountId).OfType<Guid>().Append(invoice.CreatedByAccountId).Distinct().ToList();
        var names = await accounts.GetDisplayNamesAsync(people, cancellationToken);
        var lines = invoice.Lines.OrderBy(l => l.LineNumber).Select(l => new SalesInvoiceLineView(
            l.Id, l.LineNumber, l.ProductId, l.Product.Name, l.HarvestLotId, l.HarvestLot.LotCode, l.Kg, l.UnitPrice, l.ListPrice, l.PriceSource, l.Amount, l.SoldWholeLot)).ToList();
        var payments = invoice.Payments.OrderBy(p => p.CreatedAt).ThenBy(p => p.Code).Select(p => new CustomerPaymentView(
            p.Id, p.Code, p.Kind, p.Method, p.Amount, p.PaymentDate, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId),
            p.CollectedByAccountId is { } collector ? Person(collector, names) : null)).ToList();
        var sendings = await SendingsAsync(invoice.Id, cancellationToken);
        var latest = LatestStatus(sendings);
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
            RowVersionGuard.Encode(invoice.RowVersion));
    }

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
