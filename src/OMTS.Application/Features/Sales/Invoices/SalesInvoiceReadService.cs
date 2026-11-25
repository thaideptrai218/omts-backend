using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Traceability.Publishing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// One invoice (SALE-09.1, also opened from the sale notice or after the save; the list is <see cref="SalesInvoiceSearchService"/>), and the check that comes before the QR code of a finished sale.
/// </summary>
public sealed class SalesInvoiceReadService(
    ISalesInvoiceRepository invoices,
    SalesInvoiceViews views,
    IProvenancePublisher provenance,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog)
{
    /// <summary>The invoice in a zone the caller may view invoices of (BR-SALE-08): another zone answers 403 MSG-GEN-08.</summary>
    public async Task<Result<SalesInvoiceView>> GetAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null)
        {
            return AppError.NotFound;
        }

        if (!await MayViewAsync(invoice, cancellationToken))
        {
            return AppError.Forbidden;
        }

        return await views.ViewAsync(invoice, cancellationToken);
    }

    /// <summary>
    /// TRACE-01.0 steps 1-2 at the extension point "QR code requested" (SALE-05.9): the lots of the invoice with their state and what each Not published lot misses, before the
    /// Zone Manager confirms with a Yes to MSG-SALE-07 (POST finish). For whoever may end the sale: SALES_INVOICES Create in the invoice zone. Changes nothing.
    /// </summary>
    public async Task<Result<InvoicePublishCheck>> PublishCheckAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, invoice.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (invoice.StatusId != StatusIds.SalesInvoice.Completed)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", statusCatalog.DisplayNameOf(invoice.StatusId)));
        }

        // The sale has ended (finish ran): a Yes now publishes nothing and gives no QR code (SALE-05.11, BR-TRACE-02), so the check promises neither.
        if (invoice.QrDecidedAt is not null)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", "finished"));
        }

        var lots = await provenance.CheckInvoiceLotsAsync(invoice.Lines.Select(l => l.HarvestLotId).Distinct().ToList(), cancellationToken);
        var getsQrCode = lots.Any(l => l.State == HarvestLotOriginState.Published || (l.State == HarvestLotOriginState.NotPublished && l.Missing.Count == 0));
        return new InvoicePublishCheck(invoice.Id, invoice.Code, InvoiceLotOriginView.Of(lots), getsQrCode);
    }

    /// <summary>The caller may view invoices of the zone of <paramref name="invoice"/> (BR-SALE-08, GFR-02): GET answers 403 MSG-GEN-08 when not, and so does a repeated save.</summary>
    public Task<bool> MayViewAsync(SalesInvoice invoice, CancellationToken cancellationToken) =>
        zoneAccess.AllowsAsync(PermissionFunctions.SalesInvoices, PermissionAction.View, invoice.ZoneId, cancellationToken);
}
