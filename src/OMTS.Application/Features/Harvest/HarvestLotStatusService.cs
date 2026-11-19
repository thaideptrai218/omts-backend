using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Sales.Products;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>Why a Closed lot is reopened (BR-HARVEST-04): the system for the first three causes, a Zone Manager or the Owner by hand for the last.</summary>
public enum HarvestLotReopenCause
{
    /// <summary>CLAIM-03 puts oysters that can be sold again back into the lot, whatever closed it.</summary>
    SalesReturnBackForSale,

    /// <summary>SALE-08 cancels the invoice that closed the lot.</summary>
    SalesInvoiceCancelled,

    /// <summary>SALE-07 removes the tick that closed the lot.</summary>
    SoldWholeTickRemoved,

    /// <summary>A Zone Manager of the zone or the Owner reopens a lot closed Unsold, Spoiled or by the time limit, with a reason.</summary>
    ReopenedByHand,
}

/// <summary>
/// The status changes of a lot that SALE, CLAIM and HARVEST-02 make (BR-HARVEST-04), inside the caller's unit of work: nothing here saves, the caller's one save holds the change and its audit row.
/// Lots come tracked with their raft and product lines (<see cref="IHarvestLotRepository.GetForEditAsync"/>); each change is part of the row version check of the lot, so of a sale and a parallel close one fails.
/// Every close takes what is left of each product of the lot off the product ledger (a LotClose row), and every reopen undoes those rows (<see cref="ProductStockLedger"/>), in the same save.
/// </summary>
public sealed class HarvestLotStatusService(IStatusCatalog statusCatalog, AuditLogService audit, ProductStockLedger productLedger, IUnitOfWork unitOfWork)
{
    public const string CloseReasonField = "Close reason";
    public const string LeftoverKgField = "Leftover kg";

    /// <summary>SALE-05: every lot chosen is still for sale, else MSG-GEN-09 naming the first that is not, null when they are. Each is marked changed, so a parallel close makes one side fail.</summary>
    public AppError? EnsureForSale(IEnumerable<HarvestLot> lots)
    {
        var chosen = lots.ToList();
        if (chosen.FirstOrDefault(l => !l.IsForSale) is { } closed)
        {
            return NotForSale(closed);
        }

        chosen.ForEach(unitOfWork.MarkChanged);
        return null;
    }

    /// <summary>The "sold the whole lot" tick (SALE-05, SALE-07): Closed with a leftover of 0, and the invoice named, so only it reopens the lot.</summary>
    public async Task<AppError?> CloseSoldWholeLotAsync(HarvestLot lot, DateOnly date, Guid salesInvoiceId, CancellationToken cancellationToken = default)
    {
        if (!lot.IsForSale)
        {
            return NotForSale(lot);
        }

        await SetClosedAsync(lot, date, 0m, HarvestLotCloseReason.SoldWholeLot, salesInvoiceId, cancellationToken: cancellationToken);
        return null;
    }

    /// <summary>
    /// HARVEST-02, also from CLAIM-03, and the time limit of GFR-05: Closed with the leftover kg of 0 or more. A leftover above 0 needs a reason; the sold-whole reason belongs to <see cref="CloseSoldWholeLotAsync"/>.
    /// </summary>
    /// <param name="actorAccountId">The account named in the audit log; null for the current user or the system.</param>
    public async Task<AppError?> CloseAsync(
        HarvestLot lot, DateOnly date, decimal leftoverKg, HarvestLotCloseReason? reason, Guid? actorAccountId = null, CancellationToken cancellationToken = default)
    {
        if (!lot.IsForSale)
        {
            return NotForSale(lot);
        }

        if (leftoverKg < 0)
        {
            return AppError.Invalid(LeftoverKgField);
        }

        if (reason == HarvestLotCloseReason.SoldWholeLot || (leftoverKg > 0 && reason is null))
        {
            return AppError.Invalid(CloseReasonField);
        }

        await SetClosedAsync(lot, date, leftoverKg, reason, null, actorAccountId, cancellationToken);
        return null;
    }

    /// <summary>
    /// A Zone Manager of the zone or the Owner may reopen the Closed lot by hand: it was closed Unsold, Spoiled, by the time limit or with nothing left, never by an invoice (a sold-whole-lot close reopens only
    /// through its invoice).
    /// </summary>
    public static bool MayReopenByHand(HarvestLot lot) =>
        lot.StatusId == StatusIds.HarvestLot.Closed && lot.ClosedBySalesInvoiceId is null && lot.CloseReason != HarvestLotCloseReason.SoldWholeLot;

    /// <summary>
    /// Closed to Reopened: the days before automatic close count from <paramref name="date"/>, the earlier close stays only in the audit log (BR-HARVEST-04) and the LotClose rows of the lot on the product ledger
    /// are undone. False, with nothing changed, when the lot is not Closed, when the cause is an invoice other than the one that closed it, or when a lot closed by an invoice is reopened by hand.
    /// </summary>
    /// <param name="salesInvoiceId">The invoice cancelled or corrected, for those two causes.</param>
    /// <param name="reason">The reason typed for <see cref="HarvestLotReopenCause.ReopenedByHand"/>; it is the reason of the audit row.</param>
    public async Task<bool> ReopenAsync(
        HarvestLot lot, DateOnly date, HarvestLotReopenCause cause, Guid? salesInvoiceId = null, string? reason = null, CancellationToken cancellationToken = default)
    {
        var allowed = cause switch
        {
            HarvestLotReopenCause.SalesReturnBackForSale => lot.StatusId == StatusIds.HarvestLot.Closed,
            HarvestLotReopenCause.ReopenedByHand => MayReopenByHand(lot),
            _ => lot.StatusId == StatusIds.HarvestLot.Closed && salesInvoiceId is not null && lot.ClosedBySalesInvoiceId == salesInvoiceId,
        };
        if (!allowed)
        {
            return false;
        }

        var before = HarvestLotAudit.CloseFacts(lot, statusCatalog.CodeOf(lot.StatusId));
        lot.StatusId = StatusIds.HarvestLot.Reopened;
        lot.ReopenedOn = date;
        lot.CloseDate = null;
        lot.LeftoverKg = null;
        lot.CloseReason = null;
        lot.ClosedBySalesInvoiceId = null;
        // The kg the close took off the stock of the products come back.
        await productLedger.ReverseAsync(lot.Id, ProductStockMovementKind.LotClose, cancellationToken);
        audit.Record(HarvestLotAudit.Entry(HarvestLotAudit.Reopened, lot, lot.Raft.ZoneId) with
        {
            Before = before,
            After = HarvestLotAudit.CloseFacts(lot, statusCatalog.CodeOf(lot.StatusId)),
            Reason = reason ?? cause.ToString(),
        });
        return true;
    }

    private async Task SetClosedAsync(
        HarvestLot lot, DateOnly date, decimal leftoverKg, HarvestLotCloseReason? reason, Guid? salesInvoiceId, Guid? actorAccountId = null, CancellationToken cancellationToken = default)
    {
        var before = HarvestLotAudit.CloseFacts(lot, statusCatalog.CodeOf(lot.StatusId));
        lot.StatusId = StatusIds.HarvestLot.Closed;
        lot.CloseDate = date;
        lot.LeftoverKg = leftoverKg;
        lot.CloseReason = reason;
        lot.ClosedBySalesInvoiceId = salesInvoiceId;
        // What is left of each product on the lot leaves the stock: a closed lot holds none.
        await productLedger.PostLotCloseAsync(lot, cancellationToken);
        audit.Record(HarvestLotAudit.Entry(HarvestLotAudit.Closed, lot, lot.Raft.ZoneId) with
        {
            Before = before,
            After = HarvestLotAudit.CloseFacts(lot, statusCatalog.CodeOf(lot.StatusId)),
            ActorAccountId = actorAccountId,
        });
    }

    /// <summary>MSG-GEN-09 naming the lot and its status: it is no longer for sale.</summary>
    public AppError NotForSale(HarvestLot lot) => lot.StatusId == StatusIds.HarvestLot.AwaitingWeighing
        ? AppError.Conflict(UserMessages.HarvestLotNotWeighed(lot.LotCode))
        : AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", statusCatalog.DisplayNameOf(lot.StatusId)));
}
