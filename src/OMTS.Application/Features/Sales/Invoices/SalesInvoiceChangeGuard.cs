using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The first step of SALE-07 and SALE-08: refuses what <see cref="SalesInvoiceAllowedActions"/> does not offer, then checks the version the client read (NFR-09). Every correction and
/// cancellation names the version, and a save that loses to a parallel one fails on it: nothing is applied twice and nothing retries by itself.
/// </summary>
public sealed class SalesInvoiceChangeGuard(ZoneAccessGuard zoneAccess, IStatusCatalog statusCatalog, RowVersionGuard rowVersions)
{
    /// <summary>404, 403 MSG-GEN-08, 409 MSG-GEN-09 (not Completed), 409 MSG-SALE-20 (sales return), or MSG06; null when the change can go on.</summary>
    public async Task<AppError?> RefusalAsync(SalesInvoice? invoice, string? rowVersion, CancellationToken cancellationToken)
    {
        if (invoice is null)
        {
            return AppError.NotFound;
        }

        var isOwner = await zoneAccess.IsCooperativeOwnerAsync(cancellationToken);
        return SalesInvoiceAllowedActions.ChangeRefusal(invoice.StatusId, SalesInvoiceAllowedActions.HasSalesReturn(invoice), isOwner) switch
        {
            SalesInvoiceChangeRefusal.NotCooperativeOwner => AppError.Forbidden,
            SalesInvoiceChangeRefusal.NotCompleted => AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", statusCatalog.DisplayNameOf(invoice.StatusId))),
            SalesInvoiceChangeRefusal.HasSalesReturn => AppError.Conflict(UserMessages.InvoiceHasSalesReturn(invoice.Code)),
            _ => await rowVersions.CheckAsync(invoice, rowVersion, cancellationToken),
        };
    }
}
