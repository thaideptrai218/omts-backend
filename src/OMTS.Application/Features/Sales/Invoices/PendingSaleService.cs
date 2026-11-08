using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-05 step 7 and SALE-05.0.E4 at the port: the sale awaiting the transfer of its VietQR code is polled until the bank notification saves the invoice (step 8), and can be
/// stopped (step 3 of E4). Changing its payment method is <see cref="SalesInvoiceService.ChangePaymentAsync"/>.
/// </summary>
public sealed class PendingSaleService(
    IPendingSaleRepository sales,
    PendingSaleWriter writer,
    PendingSaleViews views,
    ZoneAccessGuard zoneAccess,
    IUnitOfWork unitOfWork)
{
    public const string ObjectName = "Sale";

    /// <summary>The poll of the port: the status, the VietQR code while Awaiting, the invoice and MSG-SALE-06 once saved.</summary>
    public async Task<Result<PendingSaleView>> GetAsync(string code, CancellationToken cancellationToken)
    {
        if (await sales.GetAsync(code, cancellationToken) is not { } sale)
        {
            return AppError.NotFound;
        }

        return await views.MayUseAsync(sale, cancellationToken) ? await views.ViewAsync(sale, cancellationToken) : AppError.Forbidden;
    }

    /// <summary>The sales still awaiting their transfer in the caller's zones, oldest first: an app that was closed finds its sale again.</summary>
    public async Task<IReadOnlyList<PendingSaleView>> ListAwaitingAsync(CancellationToken cancellationToken)
    {
        var permitted = await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, cancellationToken);
        if (permitted.IsEmpty)
        {
            return [];
        }

        return await views.ViewAsync(await sales.ListAwaitingAsync(permitted.AllZones ? null : permitted.ZoneIds, cancellationToken), cancellationToken);
    }

    /// <summary>SALE-05.0.E4 step 3: 409 MSG-SALE-14 asks first (<c>confirmationField</c> = <c>confirmed</c>); with Yes nothing is saved but the code in the audit log.</summary>
    public async Task<Result<PendingSaleView>> StopAsync(string code, StopPendingSaleRequest request, CancellationToken cancellationToken)
    {
        if (await sales.GetForEditAsync(code, cancellationToken) is not { } sale)
        {
            return AppError.NotFound;
        }

        if (!await views.MayUseAsync(sale, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (sale.Status != PendingSaleStatus.Awaiting)
        {
            return NotAwaiting(sale);
        }

        if (!request.Confirmed)
        {
            return AppError.Conflict(UserMessages.StopSaleQuestion(sale.Code), new Dictionary<string, object?> { ["confirmationField"] = "confirmed" });
        }

        writer.Stop(sale);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // The transfer arrived (or the payment was changed) at this moment: the sale is no longer stopped, and the answer says how it ended.
            unitOfWork.DiscardChanges();
            return await sales.GetAsync(code, cancellationToken) is { } now ? NotAwaiting(now) : AppError.NotFound;
        }

        return await views.ViewAsync(sale, cancellationToken);
    }

    /// <summary>MSG-GEN-09: the sale already ended (GBR-01).</summary>
    public static AppError NotAwaiting(PendingSale sale) => AppError.Conflict(
        UserMessages.WrongStatus($"{ObjectName} {sale.Code}", StatusText(sale.Status)),
        new Dictionary<string, object?> { ["status"] = sale.Status.ToString(), ["invoiceId"] = sale.InvoiceId });

    /// <summary>As MSG-GEN-09 names it: a sale saved after a change of payment method is Completed too; <c>status</c> in the answer tells the two apart.</summary>
    public static string StatusText(PendingSaleStatus status) => status switch
    {
        PendingSaleStatus.Awaiting => "Awaiting transfer",
        PendingSaleStatus.Completed or PendingSaleStatus.PaymentChanged => "Completed",
        _ => "Stopped",
    };
}
