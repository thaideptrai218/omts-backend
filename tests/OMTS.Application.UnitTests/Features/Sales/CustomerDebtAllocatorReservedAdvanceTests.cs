using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>
/// An advance on an order that is still open is reserved for that order: it lowers the customer debt (the customer has paid) but no oldest-first or other automatic allocation reaches it, and only parts set
/// against invoices of the same order cover anything. Once the order is Completed or Cancelled what is left is general credit and the normal allocation applies.
/// </summary>
public class CustomerDebtAllocatorReservedAdvanceTests
{
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly Guid Order = Guid.NewGuid();
    private static readonly Guid OtherOrder = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 12);
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(7));

    private static CustomerLedgerInvoice Invoice(string code, int day, decimal total, Guid? order = null) =>
        new(Customer, Guid.NewGuid(), code, Start.AddDays(day), DateOnly.FromDateTime(Start.AddDays(day).DateTime), total, false, SalesOrderId: order);

    /// <summary>An advance of <paramref name="amount"/> on <see cref="Order"/> (or another order), held by the order unless <paramref name="orderOpen"/> is false.</summary>
    private static CustomerLedgerPayment Advance(
        decimal amount, bool orderOpen = true, int statusId = StatusIds.CustomerPayment.Completed, Guid? order = null, params (Guid InvoiceId, decimal Amount)[] parts) =>
        new(Customer, Guid.NewGuid(), "PT" + amount, CustomerPaymentKind.Payment, DateOnly.FromDateTime(Start.AddDays(5).DateTime), amount, CustomerPaymentMethod.Cash, null, null, statusId,
            PaidAt: Start.AddDays(5), Allocations: parts.Select(p => new CustomerLedgerAllocation(p.InvoiceId, p.Amount)).ToList(),
            SalesOrderId: order ?? Order, SalesOrderCode: order == OtherOrder ? "DH000002" : "DH000001", OrderHoldsAdvance: orderOpen);

    private static CustomerLedgerPayment Payment(decimal amount) =>
        new(Customer, Guid.NewGuid(), "PT" + amount, CustomerPaymentKind.Payment, DateOnly.FromDateTime(Start.AddDays(6).DateTime), amount, CustomerPaymentMethod.Cash, null, null,
            StatusIds.CustomerPayment.Completed, PaidAt: Start.AddDays(6));

    [Fact]
    public void An_advance_on_an_open_order_lowers_the_debt_but_pays_no_invoice_of_another_order()
    {
        var old = Invoice("HD000001", 0, 1_000_000m);

        var allocation = CustomerDebtAllocator.Allocate([old], [Advance(400_000m)], Today);

        // The customer has paid 400,000, so the debt is 600,000; the old invoice is still unpaid in full and the advance is listed apart.
        Assert.Equal(600_000m, allocation.Debt);
        Assert.Equal([("HD000001", 1_000_000m)], allocation.UnpaidInvoices.Select(u => (u.Code, u.Unpaid)));
        Assert.Equal(1_000_000m, allocation.DebtApartFromReserved);
        Assert.Equal([new ReservedOrderAdvance(Order, "DH000001", 400_000m)], allocation.ReservedAdvances);
        Assert.Equal(400_000m, allocation.ReservedTotal);
    }

    [Fact]
    public void The_unpaid_invoices_add_up_to_the_debt_apart_from_the_reserved_advances()
    {
        var a = Invoice("HD000001", 0, 1_000_000m);
        var b = Invoice("HD000002", 1, 500_000m);

        var allocation = CustomerDebtAllocator.Allocate([a, b], [Advance(700_000m), Payment(300_000m)], Today);

        Assert.Equal(500_000m, allocation.Debt);
        Assert.Equal(1_200_000m, allocation.UnpaidInvoices.Sum(u => u.Unpaid));
        Assert.Equal(allocation.DebtApartFromReserved, allocation.UnpaidInvoices.Sum(u => u.Unpaid));
    }

    [Fact]
    public void A_general_payment_still_covers_the_oldest_invoices_while_an_advance_stays_reserved()
    {
        var a = Invoice("HD000001", 0, 1_000_000m);
        var b = Invoice("HD000002", 1, 500_000m);

        var allocation = CustomerDebtAllocator.Allocate([b, a], [Advance(900_000m), Payment(300_000m)], Today);

        Assert.Equal([("HD000001", 700_000m), ("HD000002", 500_000m)], allocation.UnpaidInvoices.Select(u => (u.Code, u.Unpaid)));
        Assert.Equal(900_000m, allocation.ReservedTotal);
    }

    [Fact]
    public void An_advance_bigger_than_the_debt_is_not_money_owed_back_apart_from_the_reservation()
    {
        var allocation = CustomerDebtAllocator.Allocate([], [Advance(500_000m)], Today);

        // The debt is below 0 (the customer paid), but the money is held for its order: nothing is free to pay back and no invoice is unpaid.
        Assert.Equal(-500_000m, allocation.Debt);
        Assert.Equal(0m, allocation.DebtApartFromReserved);
        Assert.Empty(allocation.UnpaidInvoices);
        Assert.Null(allocation.DebtAgeDays);
    }

    [Fact]
    public void An_advance_is_released_as_general_credit_when_its_order_is_cancelled_or_completed()
    {
        var old = Invoice("HD000001", 0, 1_000_000m);

        var held = CustomerDebtAllocator.Allocate([old], [Advance(400_000m, orderOpen: true)], Today);
        // Cancelled and Completed orders no longer hold the advance: the same payment now covers the oldest invoice.
        var released = CustomerDebtAllocator.Allocate([old], [Advance(400_000m, orderOpen: false)], Today);

        Assert.Equal(1_000_000m, held.UnpaidInvoices.Single().Unpaid);
        Assert.Equal(600_000m, released.UnpaidInvoices.Single().Unpaid);
        Assert.Equal(held.Debt, released.Debt);
        Assert.Empty(released.ReservedAdvances);
    }

    [Fact]
    public void Releasing_on_cancel_and_on_complete_apply_the_same_oldest_first_rule_to_what_is_left()
    {
        var a = Invoice("HD000001", 0, 300_000m);
        var b = Invoice("HD000002", 1, 300_000m);

        // The order's own invoice took 100,000 of the advance (a part against it); 400,000 was left. Once the order ends the 400,000 covers the oldest invoices.
        var own = Invoice("HD000003", 2, 100_000m, Order);
        var part = Advance(500_000m, orderOpen: false, parts: (own.Id, 100_000m));

        var allocation = CustomerDebtAllocator.Allocate([a, b, own], [part], Today);

        Assert.Equal([("HD000002", 200_000m)], allocation.UnpaidInvoices.Select(u => (u.Code, u.Unpaid)));
        Assert.Equal(200_000m, allocation.Debt);
    }

    [Fact]
    public void Parts_set_against_invoices_of_the_same_order_cover_them_and_the_rest_stays_reserved()
    {
        var old = Invoice("HD000001", 0, 1_000_000m);
        var own = Invoice("HD000002", 1, 300_000m, Order);

        var allocation = CustomerDebtAllocator.Allocate([old, own], [Advance(500_000m, parts: (own.Id, 300_000m))], Today);

        Assert.Equal([("HD000001", 1_000_000m)], allocation.UnpaidInvoices.Select(u => (u.Code, u.Unpaid)));
        Assert.Equal([new ReservedOrderAdvance(Order, "DH000001", 200_000m)], allocation.ReservedAdvances);
        Assert.Equal(800_000m, allocation.Debt);
    }

    [Fact]
    public void A_part_of_a_reserved_advance_against_an_invoice_of_another_order_or_of_no_order_covers_nothing()
    {
        var plain = Invoice("HD000001", 0, 400_000m);
        var foreign = Invoice("HD000002", 1, 400_000m, OtherOrder);

        var allocation = CustomerDebtAllocator.Allocate([plain, foreign], [Advance(500_000m, parts: [(plain.Id, 200_000m), (foreign.Id, 300_000m)])], Today);

        Assert.Equal([("HD000001", 400_000m), ("HD000002", 400_000m)], allocation.UnpaidInvoices.Select(u => (u.Code, u.Unpaid)));
        Assert.Equal(500_000m, allocation.ReservedTotal);
    }

    [Fact]
    public void Each_order_holds_its_own_advances_and_the_list_names_them()
    {
        var allocation = CustomerDebtAllocator.Allocate(
            [Invoice("HD000001", 0, 100_000m)], [Advance(200_000m), Advance(50_000m), Advance(70_000m, order: OtherOrder)], Today);

        Assert.Equal(
            [new ReservedOrderAdvance(Order, "DH000001", 250_000m), new ReservedOrderAdvance(OtherOrder, "DH000002", 70_000m)],
            allocation.ReservedAdvances);
        Assert.Equal(100_000m, allocation.UnpaidInvoices.Single().Unpaid);
    }

    [Fact]
    public void An_advance_that_is_cancelled_or_waits_for_its_transfer_counts_nowhere_and_holds_nothing()
    {
        var old = Invoice("HD000001", 0, 100_000m);

        var allocation = CustomerDebtAllocator.Allocate(
            [old], [Advance(60_000m, statusId: StatusIds.CustomerPayment.Cancelled), Advance(40_000m, statusId: StatusIds.CustomerPayment.PendingTransfer)], Today);

        Assert.Equal(100_000m, allocation.Debt);
        Assert.Empty(allocation.ReservedAdvances);
    }

    [Fact]
    public void An_advance_awaiting_confirmation_is_reserved_like_a_completed_one()
    {
        var allocation = CustomerDebtAllocator.Allocate([Invoice("HD000001", 0, 100_000m)], [Advance(60_000m, statusId: StatusIds.CustomerPayment.AwaitingConfirmation)], Today);

        Assert.Equal(40_000m, allocation.Debt);
        Assert.Equal(60_000m, allocation.ReservedTotal);
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_of_the_rows()
    {
        var a = Invoice("HD000001", 0, 1_000_000m);
        var b = Invoice("HD000002", 1, 500_000m);
        var payments = new[] { Advance(300_000m), Payment(200_000m), Advance(80_000m, order: OtherOrder) };

        var one = CustomerDebtAllocator.Allocate([a, b], payments, Today);
        var other = CustomerDebtAllocator.Allocate([b, a], payments.Reverse(), Today);

        Assert.Equal(one.UnpaidInvoices, other.UnpaidInvoices);
        Assert.Equal(one.ReservedAdvances, other.ReservedAdvances);
        Assert.Equal(one.Debt, other.Debt);
    }
}
