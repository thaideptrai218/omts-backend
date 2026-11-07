using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>Why the system reopens a Closed lot (BR-HARVEST-04).</summary>
public enum HarvestLotReopenCause
{
    /// <summary>CLAIM-03 puts oysters that can be sold again back into the lot, whatever closed it.</summary>
    SalesReturnBackForSale,

    /// <summary>SALE-08 cancels the invoice that closed the lot.</summary>
    SalesInvoiceCancelled,

    /// <summary>SALE-07 removes the tick that closed the lot.</summary>
    SoldWholeTickRemoved,
}

/// <summary>
/// The status changes of a lot that SALE, CLAIM and HARVEST-02 make (BR-HARVEST-04), inside the caller's unit of work: nothing here saves, the caller's one save holds the change and its audit row.
/// Lots come tracked with their raft (<see cref="IHarvestLotRepository.GetForEditAsync"/>); each change is part of the row version check of the lot, so of a sale and a parallel close one fails.
/// </summary>
public sealed class HarvestLotStatusService(IStatusCatalog statusCatalog, AuditLogService audit, IUnitOfWork unitOfWork)
{
    private const string CloseReasonField = "Close reason";
    private const string LeftoverKgField = "Leftover kg";

    /// <summary>SALE-05: every lot chosen is still for sale, else MSG-GEN-09 naming the first that is not, null when they are. Each is marked changed, so a parallel close makes one side fail.</summary>
    public AppError? EnsureForSale(IEnumerable<HarvestLot> lots)
    {
        var chosen = lots.ToList();
        if (chosen.FirstOrDefault(l => !l.IsForSale) is { } closed)
        {
            return WrongStatus(closed);
        }

        chosen.ForEach(unitOfWork.MarkChanged);
        return null;
    }

    /// <summary>The "sold the whole lot" tick (SALE-05, SALE-07): Closed with a leftover of 0, and the invoice named, so only it reopens the lot.</summary>
    public AppError? CloseSoldWholeLot(HarvestLot lot, DateOnly date, Guid salesInvoiceId)
    {
        if (!lot.IsForSale)
        {
            return WrongStatus(lot);
        }

        SetClosed(lot, date, 0m, HarvestLotCloseReason.SoldWholeLot, salesInvoiceId);
        return null;
    }

    /// <summary>
    /// HARVEST-02, also from CLAIM-03, and the time limit of GFR-05: Closed with the leftover kg of 0 or more. A leftover above 0 needs a reason; the sold-whole reason belongs to <see cref="CloseSoldWholeLot"/>.
    /// </summary>
    /// <param name="actorAccountId">The account named in the audit log; null for the current user or the system.</param>
    public AppError? Close(HarvestLot lot, DateOnly date, decimal leftoverKg, HarvestLotCloseReason? reason, Guid? actorAccountId = null)
    {
        if (!lot.IsForSale)
        {
            return WrongStatus(lot);
        }

        if (leftoverKg < 0)
        {
            return AppError.Invalid(LeftoverKgField);
        }

        if (reason == HarvestLotCloseReason.SoldWholeLot || (leftoverKg > 0 && reason is null))
        {
            return AppError.Invalid(CloseReasonField);
        }

        SetClosed(lot, date, leftoverKg, reason, null, actorAccountId);
        return null;
    }

    /// <summary>
    /// Closed to Reopened: the days before automatic close count from <paramref name="date"/>, and the earlier close stays only in the audit log (BR-HARVEST-04). False, with nothing
    /// changed, when the lot is not Closed, or when the cause is an invoice other than the one that closed it.
    /// </summary>
    /// <param name="salesInvoiceId">The invoice cancelled or corrected, for those two causes.</param>
    public bool Reopen(HarvestLot lot, DateOnly date, HarvestLotReopenCause cause, Guid? salesInvoiceId = null)
    {
        if (lot.StatusId != StatusIds.HarvestLot.Closed
            || (cause != HarvestLotReopenCause.SalesReturnBackForSale && (salesInvoiceId is null || lot.ClosedBySalesInvoiceId != salesInvoiceId)))
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
        audit.Record(HarvestLotAudit.Entry(HarvestLotAudit.Reopened, lot, lot.Raft.ZoneId) with
        {
            Before = before,
            After = HarvestLotAudit.CloseFacts(lot, statusCatalog.CodeOf(lot.StatusId)),
            Reason = cause.ToString(),
        });
        return true;
    }

    private void SetClosed(HarvestLot lot, DateOnly date, decimal leftoverKg, HarvestLotCloseReason? reason, Guid? salesInvoiceId, Guid? actorAccountId = null)
    {
        var before = HarvestLotAudit.CloseFacts(lot, statusCatalog.CodeOf(lot.StatusId));
        lot.StatusId = StatusIds.HarvestLot.Closed;
        lot.CloseDate = date;
        lot.LeftoverKg = leftoverKg;
        lot.CloseReason = reason;
        lot.ClosedBySalesInvoiceId = salesInvoiceId;
        audit.Record(HarvestLotAudit.Entry(HarvestLotAudit.Closed, lot, lot.Raft.ZoneId) with
        {
            Before = before,
            After = HarvestLotAudit.CloseFacts(lot, statusCatalog.CodeOf(lot.StatusId)),
            ActorAccountId = actorAccountId,
        });
    }

    private AppError WrongStatus(HarvestLot lot) => AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", statusCatalog.DisplayNameOf(lot.StatusId)));
}
