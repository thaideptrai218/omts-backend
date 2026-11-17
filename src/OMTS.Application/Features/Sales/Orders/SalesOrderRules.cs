using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>What the caller may do with an order (it has no zone): the Owner everything; a Zone Manager what its role holds; the Accountant reads and exports.</summary>
/// <param name="CanEdit">SALES_ORDERS Edit: change the order, finish it.</param>
/// <param name="CanDelete">SALES_ORDERS Delete: cancel it.</param>
/// <param name="CanAddAdvance">May take an advance: SALES_ORDERS Edit, or CUSTOMER_PAYMENTS Create.</param>
/// <param name="CanProcess">SALES_INVOICES Create in some zone: make an invoice from the order.</param>
public sealed record SalesOrderCaller(bool IsOwner, bool CanEdit, bool CanDelete, bool CanExport, bool CanAddAdvance, bool CanProcess);

/// <summary>The rules of an order of goods as pure functions, so they are unit tested and every screen gives one answer: its money, the moves of its status, what each caller may do, and which purchase requests may be ordered.</summary>
public static class SalesOrderRules
{
    public const int MaxLines = 100;
    public const int MaxNoteLength = 1000;
    public const int MaxLineNoteLength = 500;
    public const int MaxKeyLength = 100;

    /// <summary>Draft or Delivering: the order can still change, and its advance is held for it.</summary>
    public static bool IsOpen(int statusId) => statusId is StatusIds.SalesOrder.Draft or StatusIds.SalesOrder.Delivering;

    /// <summary>While the order is open its advance is reserved for its own invoices; once it is Completed or Cancelled what is left is general credit.</summary>
    public static bool HoldsAdvance(int statusId) => IsOpen(statusId);

    public static bool AllowsEdit(int statusId) => IsOpen(statusId);

    /// <summary>"Kết thúc": only an order being delivered is finished by hand.</summary>
    public static bool AllowsFinish(int statusId) => statusId == StatusIds.SalesOrder.Delivering;

    public static bool AllowsCancel(int statusId) => IsOpen(statusId);

    /// <summary>
    /// An order with an invoice that is not cancelled cannot be cancelled (MSG-ORDER-01): the Owner finishes it. <paramref name="hasLiveInvoice"/> comes from
    /// <see cref="OMTS.Application.Common.Interfaces.Persistence.ISalesOrderRepository.HasLiveInvoiceAsync"/>.
    /// </summary>
    public static bool BlocksCancel(bool hasLiveInvoice) => hasLiveInvoice;

    /// <summary>An advance is taken on an open order of a customer with a profile: a walk-in buyer has none to hold the money.</summary>
    public static bool AllowsAdvance(int statusId, bool hasCustomer) => IsOpen(statusId) && hasCustomer;

    /// <summary>
    /// A purchase request may be made into an order while it waits for the Owner, or when it was Accepted before orders existed and no invoice took it in full. Declined, Cancelled and already Ordered ones may not.
    /// </summary>
    public static bool RequestAllowsOrder(int requestStatusId, bool fulfilled) =>
        requestStatusId == StatusIds.PurchaseRequest.AwaitingResponse || (requestStatusId == StatusIds.PurchaseRequest.Accepted && !fulfilled);

    /// <summary>The amount of a line: quantity × price, rounded half up to the dong; false when it does not fit an amount of 18 digits.</summary>
    public static bool TryLineAmount(decimal quantity, decimal unitPrice, out decimal amount) => InvoiceMoney.TryLineAmount(quantity, unitPrice, out amount);

    /// <summary>The discount is whole VND, 0 or more, and at most the subtotal, so the total is never below 0.</summary>
    public static bool DiscountFits(decimal subtotal, decimal discount) => InvoiceMoney.IsWholeAmount(discount) && discount <= subtotal;

    public static decimal Total(decimal subtotal, decimal discount) => subtotal - discount;

    /// <summary>A price other than the list price is the Owner's to decide.</summary>
    public static bool PriceNeedsOwner(decimal unitPrice, decimal listPrice, bool callerIsOwner, bool decidedByOwner) => unitPrice != listPrice && !callerIsOwner && !decidedByOwner;

    /// <summary>A discount is the Owner's to decide.</summary>
    public static bool DiscountNeedsOwner(decimal discount, bool callerIsOwner, bool decidedByOwner) => discount > 0m && !callerIsOwner && !decidedByOwner;

    /// <summary>MSG-SALE-10 for a line: a 409 whose <c>confirmationField</c> names the flag to send in the same request again.</summary>
    public static AppError PriceQuestion(string productName, string unitName, decimal listPrice, decimal unitPrice, int lineNumber) => AppError.Conflict(
        UserMessages.OrderPriceNeedsOwnerDecision(productName, unitName, listPrice, unitPrice),
        new Dictionary<string, object?> { ["confirmationField"] = $"lines[{lineNumber - 1}].priceDecidedByOwner", ["lineNumber"] = lineNumber });

    /// <summary>MSG-SALE-10 for the discount.</summary>
    public static AppError DiscountQuestion(decimal discount) => AppError.Conflict(
        UserMessages.OrderDiscountNeedsOwnerDecision(discount),
        new Dictionary<string, object?> { ["confirmationField"] = "discountDecidedByOwner" });

    /// <summary>Days an open order has waited since it was placed; null once it is Completed or Cancelled.</summary>
    // Days left until the pickup date, 0 on the day or once it has passed; null without a pickup date or once the order is closed.
    public static int? WaitingDays(int statusId, DateOnly? pickupDate, DateOnly today) =>
        IsOpen(statusId) && pickupDate is { } pickup ? Math.Max(0, pickup.DayNumber - today.DayNumber) : null;

    /// <summary>
    /// The buttons of an order for a caller. Edit and Finish need SALES_ORDERS Edit; Cancel needs Delete and no invoice that is not cancelled; an advance needs a customer profile;
    /// Process needs the right to make invoices there; PlanHarvest is the Owner's; Print needs Export.
    /// </summary>
    public static IReadOnlyList<SalesOrderAction> AllowedActions(int statusId, SalesOrderCaller caller, bool hasLiveInvoice, bool hasCustomer)
    {
        var actions = new List<SalesOrderAction>();
        if (AllowsEdit(statusId) && (caller.IsOwner || caller.CanEdit))
        {
            actions.Add(SalesOrderAction.Edit);
        }

        if (AllowsFinish(statusId) && (caller.IsOwner || caller.CanEdit))
        {
            actions.Add(SalesOrderAction.Finish);
        }

        if (AllowsCancel(statusId) && !BlocksCancel(hasLiveInvoice) && (caller.IsOwner || caller.CanDelete))
        {
            actions.Add(SalesOrderAction.Cancel);
        }

        if (AllowsAdvance(statusId, hasCustomer) && (caller.IsOwner || caller.CanAddAdvance))
        {
            actions.Add(SalesOrderAction.AddAdvance);
        }

        if (IsOpen(statusId) && (caller.IsOwner || caller.CanProcess))
        {
            actions.Add(SalesOrderAction.Process);
        }

        if (IsOpen(statusId) && caller.IsOwner)
        {
            actions.Add(SalesOrderAction.PlanHarvest);
        }

        if (caller.IsOwner || caller.CanExport)
        {
            actions.Add(SalesOrderAction.Print);
        }

        return actions;
    }
}
