using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>
/// How the invoices made from an order move it along: the seam the invoices call, in their own unit of work. The delivered quantities are read through <see cref="DeliveredByLineAsync"/>.
/// <list type="bullet">
/// <item>An invoice saved: <see cref="OnInvoiceSaved"/> moves a Draft order to Delivering, or, when the invoice delivers everything (or the seller ends the order), to Completed.</item>
/// <item>An invoice cancelled: <see cref="OnInvoiceCancelled"/> sends the order back to Draft when no other invoice of it stands, and opens a Completed order again (Delivering) when one does.</item>
/// <item>The advance of the order: its payments carry <c>SalesOrderId</c>; saving an invoice of the order writes parts of those payments against it (<c>CustomerPaymentAllocation</c>), which the debt allocator honours
/// for invoices of that order only (<see cref="Debts.CustomerDebtAllocator"/>). What is left once the order is Completed or Cancelled is general credit.</item>
/// </list>
/// </summary>
public sealed class SalesOrderProgress(ISalesOrderRepository orders, AuditLogService audit, IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider)
{
    /// <summary>
    /// The quantity delivered on each line of these orders (in the unit of the line), by line id: the lines of the invoices that are not cancelled and name the order line; a line with no delivery
    /// is absent. One call for a whole list.
    /// </summary>
    public Task<IReadOnlyDictionary<Guid, decimal>> DeliveredByLineAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken) =>
        orders.DeliveredByLineAsync(orderIds, null, cancellationToken);

    /// <summary>
    /// An invoice of the order was saved. Draft → Delivering; with <paramref name="completes"/> the order is Completed instead (<paramref name="byUser"/>: the seller agreed to end it, so it counts as finished by hand).
    /// A Delivering order is completed only by <paramref name="completes"/>. Returns whether the status changed. The order is saved by the caller.
    /// </summary>
    public bool OnInvoiceSaved(SalesOrder order, bool completes, bool byUser)
    {
        if (!SalesOrderRules.IsOpen(order.StatusId))
        {
            return false;
        }

        var target = completes ? StatusIds.SalesOrder.Completed : StatusIds.SalesOrder.Delivering;
        if (target == order.StatusId)
        {
            return false;
        }

        Move(order, target, completedManually: completes && byUser);
        return true;
    }

    /// <summary>
    /// An invoice of the order was cancelled. An order left with no invoice that is not cancelled is a Draft again (from Delivering or Completed); otherwise a Completed order is open again (Delivering) and a
    /// Delivering one stays. No completion is left on the order. Returns whether the status changed. The order is saved by the caller.
    /// </summary>
    /// <param name="otherLiveInvoice">Another invoice of the order that is not cancelled stands.</param>
    public bool OnInvoiceCancelled(SalesOrder order, bool otherLiveInvoice)
    {
        var target = otherLiveInvoice ? StatusIds.SalesOrder.Delivering : StatusIds.SalesOrder.Draft;
        var movable = order.StatusId == StatusIds.SalesOrder.Completed || (order.StatusId == StatusIds.SalesOrder.Delivering && target == StatusIds.SalesOrder.Draft);
        if (!movable)
        {
            return false;
        }

        Move(order, target, completedManually: false);
        return true;
    }

    private void Move(SalesOrder order, int target, bool completedManually)
    {
        var from = order.StatusId;
        order.StatusId = target;
        if (target == StatusIds.SalesOrder.Completed)
        {
            order.CompletedAt = timeProvider.GetUtcNow();
            order.CompletedByAccountId = currentUser.AccountId;
            order.CompletedManually = completedManually;
        }
        else
        {
            order.CompletedAt = null;
            order.CompletedByAccountId = null;
            order.CompletedManually = false;
        }

        unitOfWork.MarkChanged(order);
        audit.RecordStatusChange(nameof(SalesOrder), order.Id, order.Code, from, target);
    }
}
