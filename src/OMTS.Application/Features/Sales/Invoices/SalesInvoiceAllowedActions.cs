using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>A command offered on an invoice (SALE-09.1 step 3, SCR-SALE-05).</summary>
public enum SalesInvoiceAction
{
    /// <summary>SALE-06.1.</summary>
    Resend,

    /// <summary>CLAIM-03.</summary>
    CreateReturn,

    /// <summary>CLAIM-03.3.</summary>
    CancelReturn,

    /// <summary>SALE-07.</summary>
    Correct,

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
/// <param name="MayPrint">SALES_INVOICES Export in the zone: the Zone Manager, the Accountant (PM answer 12/11/2026) and the Owner.</param>
public sealed record SalesInvoiceCallerRights(bool IsCooperativeOwner, bool MaySell, bool MayCreateReturn, bool MayPrint);

/// <summary>Why SALE-07 and SALE-08 refuse an invoice; the endpoints answer this, and the same rule decides what is offered.</summary>
public enum SalesInvoiceChangeRefusal
{
    NotCooperativeOwner,
    NotCompleted,
    HasSalesReturn,
}

/// <summary>Why CLAIM-03 and CLAIM-03.3 refuse an invoice; the endpoints answer this, and the same rule decides what is offered.</summary>
public enum SalesReturnRefusal
{
    NotPermitted,
    InvoiceCancelled,
    HasSalesReturn,
    NoSalesReturn,
}

/// <summary>
/// SALE-09.1 step 3, "only the commands that the role of the user allows": the rules of BR-SALE-09 (Resend), BR-CLAIM-03 (the sales return), BR-SALE-10 and BR-SALE-11 (Correct and
/// Cancel, Owner only). Pure, so each role and state has one answer; the commands whose endpoint comes later are worked out here too, so the client does not change when they arrive.
/// </summary>
public static class SalesInvoiceAllowedActions
{
    /// <summary>BR-SALE-10 and BR-SALE-11: null when the invoice can be corrected or cancelled, which is exactly when <see cref="Of"/> offers Correct and Cancel.</summary>
    public static SalesInvoiceChangeRefusal? ChangeRefusal(int statusId, bool hasSalesReturn, bool isCooperativeOwner) =>
        !isCooperativeOwner ? SalesInvoiceChangeRefusal.NotCooperativeOwner
        : statusId != StatusIds.SalesInvoice.Completed ? SalesInvoiceChangeRefusal.NotCompleted
        : hasSalesReturn ? SalesInvoiceChangeRefusal.HasSalesReturn
        : null;

    /// <summary>"Has a sales return": a return that is not cancelled (BR-CLAIM-03), by the one rule <see cref="SalesReturn.Standing"/>. The invoice comes with its returns loaded.</summary>
    public static bool HasSalesReturn(SalesInvoice invoice) => invoice.Returns.Any(r => r.IsStanding);

    /// <summary>BR-CLAIM-03: null when a sales return can be recorded, which is exactly when <see cref="Of"/> offers CreateReturn.</summary>
    public static SalesReturnRefusal? CreateReturnRefusal(int statusId, bool hasSalesReturn, bool mayCreateReturn) =>
        !mayCreateReturn ? SalesReturnRefusal.NotPermitted
        : statusId == StatusIds.SalesInvoice.Cancelled ? SalesReturnRefusal.InvoiceCancelled
        : hasSalesReturn ? SalesReturnRefusal.HasSalesReturn
        : null;

    /// <summary>BR-CLAIM-03: null when the sales return can be cancelled, which is exactly when <see cref="Of"/> offers CancelReturn.</summary>
    public static SalesReturnRefusal? CancelReturnRefusal(bool returnStands, bool isCooperativeOwner) =>
        !isCooperativeOwner ? SalesReturnRefusal.NotPermitted
        : !returnStands ? SalesReturnRefusal.NoSalesReturn
        : null;

    public static IReadOnlyList<SalesInvoiceAction> Of(SalesInvoiceActionFacts facts, SalesInvoiceCallerRights rights)
    {
        var actions = new List<SalesInvoiceAction>();
        if (rights.MaySell && facts.StatusId != StatusIds.SalesInvoice.Cancelled && facts.SaleFinished && facts.LatestDeliveryStatusId != StatusIds.MessageDelivery.Sending)
        {
            actions.Add(SalesInvoiceAction.Resend);
        }

        if (CreateReturnRefusal(facts.StatusId, facts.HasSalesReturn, rights.MayCreateReturn) is null)
        {
            actions.Add(SalesInvoiceAction.CreateReturn);
        }

        if (CancelReturnRefusal(facts.HasSalesReturn, rights.IsCooperativeOwner) is null)
        {
            actions.Add(SalesInvoiceAction.CancelReturn);
        }

        if (ChangeRefusal(facts.StatusId, facts.HasSalesReturn, rights.IsCooperativeOwner) is null)
        {
            actions.Add(SalesInvoiceAction.Correct);
            actions.Add(SalesInvoiceAction.Cancel);
        }

        if (rights.MayPrint)
        {
            actions.Add(SalesInvoiceAction.Print);
        }

        return actions;
    }
}
