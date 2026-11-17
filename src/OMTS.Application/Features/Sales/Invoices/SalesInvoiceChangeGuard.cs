using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.QualityClaims;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The first step of an edit, a price cut and a cancellation: refuses what <see cref="SalesInvoiceAllowedActions"/> does not offer, then checks the version the client read (NFR-09). Every change names
/// the version, and a save that loses to a parallel one fails on it: nothing is applied twice and nothing retries by itself. Who may change an invoice is a permission of the zone of the invoice
/// (SALES_INVOICES Edit for an edit and a price cut, Delete for a cancellation); only the Cooperative Owner has them until another role is given them.
/// </summary>
public sealed class SalesInvoiceChangeGuard(ZoneAccessGuard zoneAccess, IStatusCatalog statusCatalog, RowVersionGuard rowVersions, IInvoiceSalesReturnCheck salesReturns)
{
    /// <summary>404, 403 MSG-GEN-08, 409 MSG-GEN-09 (not Completed), 409 MSG-SALE-20 (sales return), or MSG06; null when the change can go on.</summary>
    /// <param name="action">Edit for an edit and a price cut, Delete for a cancellation.</param>
    public async Task<AppError?> RefusalAsync(SalesInvoice? invoice, string? rowVersion, PermissionAction action, CancellationToken cancellationToken)
    {
        if (invoice is null)
        {
            return AppError.NotFound;
        }

        var permitted = await zoneAccess.AllowsAsync(PermissionFunctions.SalesInvoices, action, invoice.ZoneId, cancellationToken);
        var hasSalesReturn = await salesReturns.HasSalesReturnAsync(invoice.Id, cancellationToken);
        return SalesInvoiceAllowedActions.ChangeRefusal(invoice.StatusId, hasSalesReturn, permitted) switch
        {
            SalesInvoiceChangeRefusal.NotPermitted => AppError.Forbidden,
            SalesInvoiceChangeRefusal.NotCompleted => AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", statusCatalog.DisplayNameOf(invoice.StatusId))),
            SalesInvoiceChangeRefusal.HasSalesReturn => AppError.Conflict(UserMessages.InvoiceHasSalesReturn(invoice.Code)),
            _ => await rowVersions.CheckAsync(invoice, rowVersion, cancellationToken),
        };
    }
}
