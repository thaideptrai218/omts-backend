using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.PurchaseRequests;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// What SALE-05 reads besides the products and the lots for sale: one invoice (opened from the sale notice or after the save; the search of SALE-09 comes with PR 9), and the requests of a
/// profile the invoice can be linked to (SALE-05.3).
/// </summary>
public sealed class SalesInvoiceReadService(
    ISalesInvoiceRepository invoices,
    ICustomerRepository customers,
    IPurchaseRequestRepository requests,
    IPurchaseRequestFulfilment fulfilment,
    SalesInvoiceViews views,
    ZoneAccessGuard zoneAccess,
    TimeProvider timeProvider)
{
    /// <summary>The invoice in a zone the caller may view invoices of (BR-SALE-08): another zone answers 403 MSG-GEN-08.</summary>
    public async Task<Result<SalesInvoiceView>> GetAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.SalesInvoices, PermissionAction.View, invoice.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        return await views.ViewAsync(invoice, cancellationToken);
    }

    /// <summary>SALE-05.3 step 1: Accepted, not Fulfilled, pickup date not more than 3 days ago; with the accepted kg, pickup date and agreement to pay later.</summary>
    public async Task<Result<IReadOnlyList<OpenPurchaseRequestView>>> OpenRequestsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (await customers.GetForEditAsync(customerId, cancellationToken) is null)
        {
            return AppError.NotFound;
        }

        var earliest = timeProvider.Today().AddDays(-SalesInvoiceFactsReader.RequestPickupDaysBack);
        var accepted = await requests.ListAcceptedOfCustomerAsync(customerId, earliest, cancellationToken);
        var fulfilled = await fulfilment.FulfilledAmongAsync(accepted.Select(r => r.Id).ToList(), cancellationToken);
        IReadOnlyList<OpenPurchaseRequestView> open = accepted
            .Where(r => !fulfilled.Contains(r.Id))
            .Select(r => new OpenPurchaseRequestView(r.Id, r.ProductId, r.Product.Name, r.RequestedKg, r.AcceptedKg!.Value, r.AgreedPickupDate!.Value, r.PayLaterAgreed == true, r.ResponseNote))
            .ToList();
        return Result<IReadOnlyList<OpenPurchaseRequestView>>.Success(open);
    }
}
