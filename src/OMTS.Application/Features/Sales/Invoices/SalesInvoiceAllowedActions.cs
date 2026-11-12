using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>A command offered on an invoice (SALE-09.1 step 3, SCR-SALE-05).</summary>
public enum SalesInvoiceAction
{
    /// <summary>SALE-06.1.</summary>
    Resend,

    /// <summary>CLAIM-03 (no endpoint yet).</summary>
    CreateReturn,

    /// <summary>CLAIM-03.3 (no endpoint yet).</summary>
    CancelReturn,

    /// <summary>SALE-07: the price cut after a claim.</summary>
    Correct,

    /// <summary>The Owner changes the invoice like a purchase receipt (PUT).</summary>
    Edit,

    /// <summary>SALE-08.</summary>
    Cancel,

    /// <summary>SALE-09.2: the invoice as a file.</summary>
    Print,
}

/// <summary>The state of the invoice that decides what can be done with it.</summary>
/// <param name="LatestDeliveryStatusId">The status of the latest sending, a stale Sending already read as Not sent (BR-SALE-09).</param>
/// <param name="SaleFinished">The end of the sale has answered the QR question: before it nothing has been sent, so there is nothing to send again.</param>
/// <param name="HasSalesReturn">A sales return that is not cancelled (BR-CLAIM-03).</param>
public sealed record SalesInvoiceActionFacts(int StatusId, int LatestDeliveryStatusId, bool SaleFinished, bool HasSalesReturn);

/// <summary>What the caller may do in the zone of the invoice (GFR-02, §I.4.4).</summary>
/// <param name="MaySell">SALES_INVOICES Create in the zone: the Zone Manager of the zone and the Owner.</param>
/// <param name="MayCreateReturn">SALES_RETURNS Create in the zone: the Zone Manager of the zone and the Owner.</param>
/// <param name="MayPrint">SALES_INVOICES Export in the zone: the Accountant only views (§I.4.4, R).</param>
/// <param name="MayEdit">SALES_INVOICES Edit in the zone: the Owner only, until the right is given to another role.</param>
/// <param name="MayDelete">SALES_INVOICES Delete in the zone: cancels the invoice; the Owner only, until the right is given to another role.</param>
public sealed record SalesInvoiceCallerRights(bool IsCooperativeOwner, bool MaySell, bool MayCreateReturn, bool MayPrint, bool MayEdit = false, bool MayDelete = false);

/// <summary>Why a change (edit, price cut, cancellation) refuses an invoice; the endpoints answer this, and the same rule decides what is offered.</summary>
public enum SalesInvoiceChangeRefusal
{
    NotPermitted,
    NotCompleted,
    HasSalesReturn,
}

/// <summary>
/// SALE-09.1 step 3, "only the commands that the role of the user allows": the rules of BR-SALE-09 (Resend), BR-CLAIM-03 (the sales return), BR-SALE-10 and BR-SALE-11 (Correct and
/// Cancel, Owner only). Pure, so each role and state has one answer; the commands whose endpoint comes later are worked out here too, so the client does not change when they arrive.
/// </summary>
public static class SalesInvoiceAllowedActions
{
    /// <summary>
    /// BR-SALE-10 and BR-SALE-11: null when the caller may change the invoice (<paramref name="permitted"/>: SALES_INVOICES Edit for an edit or a price cut, Delete for a cancellation), which is exactly
    /// when <see cref="Of"/> offers the command.
    /// </summary>
    public static SalesInvoiceChangeRefusal? ChangeRefusal(int statusId, bool hasSalesReturn, bool permitted) =>
        !permitted ? SalesInvoiceChangeRefusal.NotPermitted
        : statusId != StatusIds.SalesInvoice.Completed ? SalesInvoiceChangeRefusal.NotCompleted
        : hasSalesReturn ? SalesInvoiceChangeRefusal.HasSalesReturn
        : null;

    public static IReadOnlyList<SalesInvoiceAction> Of(SalesInvoiceActionFacts facts, SalesInvoiceCallerRights rights)
    {
        var cancelled = facts.StatusId == StatusIds.SalesInvoice.Cancelled;
        var actions = new List<SalesInvoiceAction>();
        if (rights.MaySell && !cancelled && facts.SaleFinished && facts.LatestDeliveryStatusId != StatusIds.MessageDelivery.Sending)
        {
            actions.Add(SalesInvoiceAction.Resend);
        }

        if (rights.MayCreateReturn && !cancelled && !facts.HasSalesReturn)
        {
            actions.Add(SalesInvoiceAction.CreateReturn);
        }

        if (rights.IsCooperativeOwner && facts.HasSalesReturn)
        {
            actions.Add(SalesInvoiceAction.CancelReturn);
        }

        if (ChangeRefusal(facts.StatusId, facts.HasSalesReturn, rights.MayEdit) is null)
        {
            actions.Add(SalesInvoiceAction.Edit);
            actions.Add(SalesInvoiceAction.Correct);
        }

        if (ChangeRefusal(facts.StatusId, facts.HasSalesReturn, rights.MayDelete) is null)
        {
            actions.Add(SalesInvoiceAction.Cancel);
        }

        if (rights.MayPrint)
        {
            actions.Add(SalesInvoiceAction.Print);
        }

        return actions;
    }
}
