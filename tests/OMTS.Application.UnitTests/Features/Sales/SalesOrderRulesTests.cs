using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Domain.Constants;
using static OMTS.Application.Features.Sales.Orders.SalesOrderAction;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The rules of an order of goods: its money, which statuses allow what, the buttons of each caller, the questions the Owner answers, and which purchase requests may be ordered.</summary>
public class SalesOrderRulesTests
{
    private const int Draft = StatusIds.SalesOrder.Draft;
    private const int Delivering = StatusIds.SalesOrder.Delivering;
    private const int Completed = StatusIds.SalesOrder.Completed;
    private const int Cancelled = StatusIds.SalesOrder.Cancelled;

    private static readonly SalesOrderCaller Owner = new(IsOwner: true, CanEdit: true, CanDelete: true, CanExport: true, CanAddAdvance: true, CanProcess: true);
    private static readonly SalesOrderCaller Manager = new(IsOwner: false, CanEdit: true, CanDelete: false, CanExport: false, CanAddAdvance: true, CanProcess: true);
    private static readonly SalesOrderCaller Accountant = new(IsOwner: false, CanEdit: false, CanDelete: false, CanExport: true, CanAddAdvance: true, CanProcess: false);

    // ---- Money ----

    [Theory]
    [InlineData(2, 30_000, 60_000)]
    [InlineData(12.345, 30_001, 370_362)]
    [InlineData(0.5, 25_001, 12_501)]
    public void A_line_is_quantity_times_price_rounded_half_up_to_the_dong(double quantity, int price, int expected)
    {
        Assert.True(SalesOrderRules.TryLineAmount((decimal)quantity, price, out var amount));
        Assert.Equal(expected, amount);
    }

    [Fact]
    public void A_line_too_large_for_an_amount_of_18_digits_is_refused_and_not_thrown()
    {
        Assert.False(SalesOrderRules.TryLineAmount(999_999_999_999m, 999_999_999_999m, out _));
        Assert.False(SalesOrderRules.TryLineAmount(decimal.MaxValue, 2m, out _));
    }

    [Theory]
    [InlineData(1_000_000, 0, true)]
    [InlineData(1_000_000, 250_000, true)]
    [InlineData(1_000_000, 1_000_000, true)]
    [InlineData(1_000_000, 1_000_001, false)]
    [InlineData(1_000_000, -1, false)]
    public void The_discount_is_at_most_the_subtotal_so_the_total_is_never_below_zero(int subtotal, int discount, bool fits)
    {
        Assert.Equal(fits, SalesOrderRules.DiscountFits(subtotal, discount));
        if (fits)
        {
            Assert.True(SalesOrderRules.Total(subtotal, discount) >= 0);
        }
    }

    [Fact]
    public void A_discount_of_a_part_of_a_dong_is_refused() => Assert.False(SalesOrderRules.DiscountFits(1_000_000m, 100.5m));

    [Fact]
    public void The_total_is_the_subtotal_less_the_discount() => Assert.Equal(750_000m, SalesOrderRules.Total(1_000_000m, 250_000m));

    // ---- Statuses ----

    [Theory]
    [InlineData(Draft, true)]
    [InlineData(Delivering, true)]
    [InlineData(Completed, false)]
    [InlineData(Cancelled, false)]
    public void Only_a_draft_or_delivering_order_is_open_and_holds_its_advance(int status, bool open)
    {
        Assert.Equal(open, SalesOrderRules.IsOpen(status));
        Assert.Equal(open, SalesOrderRules.HoldsAdvance(status));
        Assert.Equal(open, SalesOrderRules.AllowsEdit(status));
        Assert.Equal(open, SalesOrderRules.AllowsCancel(status));
    }

    [Theory]
    [InlineData(Draft, false)]
    [InlineData(Delivering, true)]
    [InlineData(Completed, false)]
    [InlineData(Cancelled, false)]
    public void Only_a_delivering_order_is_finished_by_hand(int status, bool allowed) => Assert.Equal(allowed, SalesOrderRules.AllowsFinish(status));

    [Theory]
    [InlineData(Draft, true, true)]
    [InlineData(Draft, false, false)]
    [InlineData(Delivering, true, true)]
    [InlineData(Completed, true, false)]
    [InlineData(Cancelled, true, false)]
    public void An_advance_needs_an_open_order_and_a_customer_profile(int status, bool hasCustomer, bool allowed) =>
        Assert.Equal(allowed, SalesOrderRules.AllowsAdvance(status, hasCustomer));

    [Fact]
    public void An_invoice_that_is_not_cancelled_blocks_the_cancel_of_its_order()
    {
        Assert.True(SalesOrderRules.BlocksCancel(hasLiveInvoice: true));
        Assert.False(SalesOrderRules.BlocksCancel(hasLiveInvoice: false));
    }

    [Theory]
    [InlineData(Draft, 9, 5, 4)]
    [InlineData(Delivering, 9, 9, 0)]
    [InlineData(Delivering, 5, 9, 0)]
    [InlineData(Completed, 9, 5, null)]
    [InlineData(Cancelled, 9, 5, null)]
    public void An_open_order_counts_the_days_left_until_the_pickup_date(int status, int pickupDay, int today, int? expected) =>
        Assert.Equal(expected, SalesOrderRules.WaitingDays(status, new DateOnly(2026, 11, pickupDay), new DateOnly(2026, 11, today)));

    [Fact]
    public void An_open_order_without_a_pickup_date_has_no_waiting_days() =>
        Assert.Null(SalesOrderRules.WaitingDays(Draft, null, new DateOnly(2026, 11, 5)));

    // ---- Purchase requests ----

    [Theory]
    [InlineData(StatusIds.PurchaseRequest.AwaitingResponse, false, true)]
    [InlineData(StatusIds.PurchaseRequest.Accepted, false, true)]
    [InlineData(StatusIds.PurchaseRequest.Accepted, true, false)]
    [InlineData(StatusIds.PurchaseRequest.Declined, false, false)]
    [InlineData(StatusIds.PurchaseRequest.Cancelled, false, false)]
    [InlineData(StatusIds.PurchaseRequest.Ordered, false, false)]
    public void A_request_waiting_for_the_owner_or_accepted_and_not_fulfilled_may_be_ordered(int status, bool fulfilled, bool allowed) =>
        Assert.Equal(allowed, SalesOrderRules.RequestAllowsOrder(status, fulfilled));

    // ---- Who may press what ----

    private static void Check(int status, SalesOrderCaller caller, bool hasLiveInvoice, bool hasCustomer, params SalesOrderAction[] expected) =>
        Assert.Equal(expected, SalesOrderRules.AllowedActions(status, caller, hasLiveInvoice, hasCustomer));

    [Fact]
    public void The_owner_has_every_button_on_a_draft_order_of_a_customer() =>
        Check(Draft, Owner, false, true, Edit, Cancel, AddAdvance, Process, PlanHarvest, Print);

    [Fact]
    public void The_owner_finishes_a_delivering_order_but_cannot_cancel_it_while_an_invoice_stands()
    {
        Check(Delivering, Owner, false, true, Edit, Finish, Cancel, AddAdvance, Process, PlanHarvest, Print);
        Check(Delivering, Owner, true, true, Edit, Finish, AddAdvance, Process, PlanHarvest, Print);
    }

    [Fact]
    public void A_walk_in_order_has_no_advance_button() =>
        Check(Draft, Owner, false, false, Edit, Cancel, Process, PlanHarvest, Print);

    [Fact]
    public void The_zone_manager_changes_and_finishes_but_never_cancels_and_never_plans_a_harvest()
    {
        Check(Draft, Manager, false, true, Edit, AddAdvance, Process);
        Check(Delivering, Manager, false, true, Edit, Finish, AddAdvance, Process);
    }

    [Fact]
    public void The_accountant_reads_prints_and_takes_an_advance_and_changes_nothing()
    {
        Check(Draft, Accountant, false, true, AddAdvance, Print);
        Check(Delivering, Accountant, false, true, AddAdvance, Print);
    }

    [Theory]
    [InlineData(Completed)]
    [InlineData(Cancelled)]
    public void A_finished_order_can_only_be_printed(int status)
    {
        Check(status, Owner, false, true, Print);
        Check(status, Manager, false, true);
        Check(status, Accountant, false, true, Print);
    }

    // ---- The Owner's questions ----

    [Fact]
    public void A_price_other_than_the_list_price_is_the_owners_to_decide()
    {
        Assert.True(SalesOrderRules.PriceNeedsOwner(90_000m, 100_000m, callerIsOwner: false, decidedByOwner: false));
        Assert.False(SalesOrderRules.PriceNeedsOwner(90_000m, 100_000m, callerIsOwner: true, decidedByOwner: false));
        Assert.False(SalesOrderRules.PriceNeedsOwner(90_000m, 100_000m, callerIsOwner: false, decidedByOwner: true));
        Assert.False(SalesOrderRules.PriceNeedsOwner(100_000m, 100_000m, callerIsOwner: false, decidedByOwner: false));
    }

    [Fact]
    public void A_discount_is_the_owners_to_decide()
    {
        Assert.True(SalesOrderRules.DiscountNeedsOwner(10_000m, callerIsOwner: false, decidedByOwner: false));
        Assert.False(SalesOrderRules.DiscountNeedsOwner(0m, callerIsOwner: false, decidedByOwner: false));
        Assert.False(SalesOrderRules.DiscountNeedsOwner(10_000m, callerIsOwner: true, decidedByOwner: false));
        Assert.False(SalesOrderRules.DiscountNeedsOwner(10_000m, callerIsOwner: false, decidedByOwner: true));
    }

    [Fact]
    public void The_price_question_names_the_flag_of_the_line_to_send_again()
    {
        var error = SalesOrderRules.PriceQuestion("Hàu sữa", "Lồng", 1_200_000m, 1_000_000m, lineNumber: 3);

        Assert.Equal(ErrorKind.Conflict, error.Kind);
        Assert.Equal(MessageCodes.Sale10, error.Message.Code);
        Assert.Equal("lines[2].priceDecidedByOwner", error.Details!["confirmationField"]);
        Assert.Equal(3, error.Details["lineNumber"]);
    }

    [Fact]
    public void The_discount_question_names_its_flag()
    {
        var error = SalesOrderRules.DiscountQuestion(50_000m);

        Assert.Equal(MessageCodes.Sale10, error.Message.Code);
        Assert.Equal("discountDecidedByOwner", error.Details!["confirmationField"]);
    }

    // ---- Messages ----

    [Fact]
    public void The_order_messages_carry_their_codes_and_say_what_the_user_needs()
    {
        var blocked = UserMessages.OrderHasInvoices("DH000007");
        Assert.Equal(MessageCodes.Order01, blocked.Code);
        Assert.Contains("DH000007", blocked.Text);
        Assert.Contains("Kết thúc", blocked.Text);

        var ordered = UserMessages.PurchaseRequestAlreadyOrdered("DH000007");
        Assert.Equal(MessageCodes.Order02, ordered.Code);
        Assert.Contains("DH000007", ordered.Text);

        var below = UserMessages.OrderQuantityBelowDelivered("Hàu sữa", "kg", 60m);
        Assert.Equal(MessageCodes.Order03, below.Code);
        Assert.Contains("60", below.Text);
    }

    [Fact]
    public void The_notice_to_the_trader_names_the_order_and_the_pickup_time_date_first()
    {
        var notice = UserMessages.OrderPlacedNotice("HTX Trung Nam", "DH000007", new DateTimeOffset(2026, 11, 14, 8, 30, 0, TimeSpan.FromHours(7)));

        Assert.Equal(MessageCodes.Order04, notice.Code);
        Assert.Equal("HTX Trung Nam: Đã đặt hàng DH000007, hẹn lấy 08:30 14/11/2026.", notice.Text);
        Assert.Equal("DH000007", notice.Parameters!["order_code"]);
        Assert.Equal("HTX Trung Nam: Đã đặt hàng DH000007.", UserMessages.OrderPlacedNotice("HTX Trung Nam", "DH000007", null).Text);
    }
}
