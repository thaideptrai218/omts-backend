using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>
/// CLAIM-03.3 – the Cooperative Owner cancels a sales return recorded by mistake, with a reason (the API lets the Owner only). It then counts nowhere (BR-CLAIM-03): not in the debt or the reports,
/// its kg leave the lot figures, the discarded lot is listed on the provenance page again and the invoice can be corrected or cancelled again. Each lot keeps its status, whatever happened to it
/// since, so nothing is reopened or closed. Before the remaining kg of a lot goes below zero the Owner is warned (MSG-CLAIM-06). A cancellation sends no notification.
/// </summary>
public sealed class SalesReturnCancellationService(
    ISalesReturnRepository salesReturns,
    IHarvestLotSalesQuery lotSales,
    SalesReturnViews views,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string CancelledAction = "SalesReturn.Cancelled";

    public async Task<Result<SalesReturnView>> CancelAsync(Guid returnId, CancelSalesReturnRequest request, CancellationToken cancellationToken)
    {
        var salesReturn = await salesReturns.GetForEditAsync(returnId, cancellationToken);
        if (salesReturn is null)
        {
            return AppError.NotFound;
        }

        var refusal = SalesInvoiceAllowedActions.CancelReturnRefusal(salesReturn.IsStanding, await zoneAccess.IsCooperativeOwnerAsync(cancellationToken));
        if (refusal == SalesReturnRefusal.NotPermitted)
        {
            return AppError.Forbidden;
        }

        if (refusal == SalesReturnRefusal.NoSalesReturn)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"{SalesReturnRules.ObjectName} {salesReturn.Code}", statusCatalog.DisplayNameOf(salesReturn.StatusId)));
        }

        if (await rowVersions.CheckAsync(salesReturn, request.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        if (CustomerPaymentRules.Reason(request.Reason) is not { } reason)
        {
            return AppError.Invalid("Reason");
        }

        if (!request.ConfirmNegativeRemaining && await NegativeRemainingAsync(salesReturn, cancellationToken) is { } warning)
        {
            return warning;
        }

        var lots = salesReturn.Lines.Select(l => l.HarvestLot).DistinctBy(l => l.Id).ToList();
        lots.ForEach(unitOfWork.MarkChanged);
        var invoice = salesReturn.Invoice;
        unitOfWork.MarkChanged(invoice);
        if (invoice.Customer is { } customer)
        {
            unitOfWork.MarkChanged(customer);
        }

        salesReturn.StatusId = StatusIds.SalesReturn.Cancelled;
        salesReturn.CancelledAt = timeProvider.GetUtcNow();
        salesReturn.CancelledByAccountId = currentUser.AccountId;
        salesReturn.CancelReason = reason;
        audit.Record(new AuditEntry(CancelledAction, SalesReturnRules.RecordType)
        {
            RecordId = salesReturn.Id,
            RecordCode = salesReturn.Code,
            ZoneId = invoice.ZoneId,
            Reason = reason,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.SalesReturn.Recorded) },
            After = new { Status = statusCatalog.CodeOf(salesReturn.StatusId), salesReturn.InvoiceId, salesReturn.Value },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(salesReturn.Id, cancellationToken);
    }

    // MSG-CLAIM-06: only a lot whose oysters this return put back loses kg by the cancellation (BR-CLAIM-03); the discarded kg is not taken back in.
    private async Task<AppError?> NegativeRemainingAsync(SalesReturn salesReturn, CancellationToken cancellationToken)
    {
        var backForSale = salesReturn.Lines.Where(l => l.Outcome == SalesReturnOutcome.BackForSale).GroupBy(l => l.HarvestLot).Select(g => (Lot: g.Key, Kg: g.Sum(l => l.ReturnedKg))).ToList();
        if (backForSale.Count == 0)
        {
            return null;
        }

        var figures = await lotSales.FiguresAsync(backForSale.Select(l => l.Lot.Id).ToList(), cancellationToken);
        var negative = backForSale
            .Select(l => (l.Lot.LotCode, Remaining: SalesReturnViews.RemainingAfterCancel(l.Lot, figures.GetValueOrDefault(l.Lot.Id, LotSalesFigures.None), l.Kg)))
            .Where(l => l.Remaining < 0m)
            .OrderBy(l => l.LotCode, StringComparer.Ordinal)
            .ToList();
        if (negative.Count == 0)
        {
            return null;
        }

        var lotText = string.Join(", ", negative.Select(l => $"{l.LotCode} ({l.Remaining.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} kg)"));
        return AppError.Conflict(
            UserMessages.SalesReturnNegativeRemaining(salesReturn.Code, lotText),
            new Dictionary<string, object?> { ["confirmationField"] = "confirmNegativeRemaining", ["lots"] = negative.Select(l => new { lotCode = l.LotCode, remainingKg = l.Remaining }).ToList() });
    }
}
