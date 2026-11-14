using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>Refuses what <see cref="SalesInvoiceAllowedActions"/> does not offer for a sales return (BR-CLAIM-03, BR-FARM-08), so the offers and the refusals are one rule.</summary>
public sealed class SalesReturnGuard(ZoneAccessGuard zoneAccess, IStatusCatalog statusCatalog)
{
    /// <summary>404, 403 MSG-GEN-08 outside the zones where the caller records returns, 409 MSG-GEN-09 for a cancelled invoice or one that already has a return; null when a return can be created.</summary>
    public async Task<AppError?> CreateRefusalAsync(SalesInvoice? invoice, CancellationToken cancellationToken)
    {
        if (invoice is null)
        {
            return AppError.NotFound;
        }

        var may = await zoneAccess.AllowsAsync(PermissionFunctions.SalesReturns, PermissionAction.Create, invoice.ZoneId, cancellationToken);
        return SalesInvoiceAllowedActions.CreateReturnRefusal(invoice.StatusId, SalesInvoiceAllowedActions.HasSalesReturn(invoice), may) switch
        {
            SalesReturnRefusal.NotPermitted => AppError.Forbidden,
            SalesReturnRefusal.InvoiceCancelled => AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", statusCatalog.DisplayNameOf(StatusIds.SalesInvoice.Cancelled))),
            SalesReturnRefusal.HasSalesReturn => AppError.Conflict(UserMessages.WrongStatus($"Sales invoice {invoice.Code}", "returned")),
            _ => null,
        };
    }
}
