using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using static OMTS.Application.Features.Sales.Invoices.SalesInvoiceAction;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>SALE-09.1 step 3 for each role and state: BR-SALE-09 (Resend), BR-CLAIM-03 (the sales return), BR-SALE-10 and BR-SALE-11 (Edit, Correct and Cancel: the Owner only, by the SALES_INVOICES Edit and Delete rights).</summary>
public class SalesInvoiceAllowedActionsTests
{
    private static readonly SalesInvoiceCallerRights Owner = new(IsCooperativeOwner: true, MaySell: true, MayCreateReturn: true, MayPrint: true, MayEdit: true, MayDelete: true);
    private static readonly SalesInvoiceCallerRights Manager = new(IsCooperativeOwner: false, MaySell: true, MayCreateReturn: true, MayPrint: true);
    private static readonly SalesInvoiceCallerRights Accountant = new(IsCooperativeOwner: false, MaySell: false, MayCreateReturn: false, MayPrint: false);

    private static SalesInvoiceActionFacts Facts(
        int status = StatusIds.SalesInvoice.Completed, int delivery = StatusIds.MessageDelivery.Sent, bool finished = true, bool hasReturn = false) =>
        new(status, delivery, finished, hasReturn);

    private static void Check(SalesInvoiceCallerRights rights, SalesInvoiceActionFacts facts, params SalesInvoiceAction[] expected) =>
        Assert.Equal(expected, SalesInvoiceAllowedActions.Of(facts, rights));

    [Fact]
    public void On_a_finished_completed_invoice_the_owner_has_every_command_but_cancel_return() =>
        Check(Owner, Facts(), Resend, CreateReturn, Edit, Correct, Cancel, Print);

    [Fact]
    public void The_zone_manager_resends_returns_and_prints_and_never_corrects_or_cancels() =>
        Check(Manager, Facts(), Resend, CreateReturn, Print);

    // Who edits or cancels is a right of the zone (SALES_INVOICES Edit, Delete), not the role: a role given one of them gets that command alone.
    [Fact]
    public void Edit_and_cancel_follow_their_own_rights()
    {
        Check(Manager with { MayEdit = true }, Facts(), Resend, CreateReturn, Edit, Correct, Print);
        Check(Manager with { MayDelete = true }, Facts(), Resend, CreateReturn, Cancel, Print);
    }

    [Fact]
    public void The_accountant_is_offered_no_command() =>
        Check(Accountant, Facts());

    // BR-SALE-09: not while the latest sending is Sending. Not sent and Sent both can be sent again.
    [Theory]
    [InlineData(StatusIds.MessageDelivery.NotSent, true)]
    [InlineData(StatusIds.MessageDelivery.Sent, true)]
    [InlineData(StatusIds.MessageDelivery.Sending, false)]
    public void Resend_follows_the_latest_sending(int delivery, bool offered)
    {
        Assert.Equal(offered, SalesInvoiceAllowedActions.Of(Facts(delivery: delivery), Owner).Contains(Resend));
        Assert.Equal(offered, SalesInvoiceAllowedActions.Of(Facts(delivery: delivery), Manager).Contains(Resend));
    }

    // Before the end of the sale nothing has been sent: POST resend would answer MSG-GEN-09.
    [Fact]
    public void An_unfinished_sale_has_nothing_to_resend()
    {
        Check(Manager, Facts(delivery: StatusIds.MessageDelivery.NotSent, finished: false), CreateReturn, Print);
        Check(Owner, Facts(delivery: StatusIds.MessageDelivery.NotSent, finished: false), CreateReturn, Edit, Correct, Cancel, Print);
    }

    [Fact]
    public void A_cancelled_invoice_can_only_be_printed()
    {
        Check(Owner, Facts(status: StatusIds.SalesInvoice.Cancelled), Print);
        Check(Manager, Facts(status: StatusIds.SalesInvoice.Cancelled), Print);
        Check(Accountant, Facts(status: StatusIds.SalesInvoice.Cancelled));
    }

    // BR-CLAIM-03: a sales return that is not cancelled blocks Correct, Cancel and a second return; only the Owner cancels it.
    [Fact]
    public void With_a_sales_return_only_the_owner_cancels_it_and_correct_and_cancel_are_blocked()
    {
        Check(Owner, Facts(hasReturn: true), Resend, CancelReturn, Print);
        Check(Manager, Facts(hasReturn: true), Resend, Print);
        Check(Accountant, Facts(hasReturn: true));
    }

    // A Cancelled invoice cannot have an active return in practice: CancelReturn would still be the only way out, so the rule is kept.
    [Fact]
    public void The_owner_cancels_a_sales_return_whatever_the_invoice_state() =>
        Check(Owner, Facts(status: StatusIds.SalesInvoice.Cancelled, hasReturn: true), CancelReturn, Print);

    // SCR-SALE-05: rights come from the zone of the invoice (a pair in another zone or another role gives none of them).
    [Fact]
    public void Print_alone_needs_the_export_right_and_return_its_own_right()
    {
        var printOnly = new SalesInvoiceCallerRights(IsCooperativeOwner: false, MaySell: false, MayCreateReturn: false, MayPrint: true);
        var returnOnly = new SalesInvoiceCallerRights(IsCooperativeOwner: false, MaySell: false, MayCreateReturn: true, MayPrint: false);

        Check(printOnly, Facts(), Print);
        Check(returnOnly, Facts(), CreateReturn);
    }
}
