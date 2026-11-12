using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>An advance on an order as a customer payment: what the payment rules accept for it, how the statement shows it, and which purchase requests the order may come from.</summary>
public class SalesOrderAdvanceRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(7));
    private static readonly Guid Customer = Guid.NewGuid();

    private static RecordCustomerPaymentRequest Request(
        decimal? amount = 500_000m, CustomerPaymentMethod? method = CustomerPaymentMethod.Cash, string? bankReference = null, decimal discount = 0m,
        IReadOnlyList<CustomerPaymentAllocationInput>? allocations = null, Guid? cashAccount = null) =>
        new(Customer, null, amount, method, bankReference, PaidAt: Now, DiscountAmount: discount, CashAccountId: cashAccount, Allocations: allocations);

    private static List<string> Invalid(RecordCustomerPaymentRequest request, CustomerPaymentMode mode = CustomerPaymentMode.OrderAdvance) =>
        CustomerPaymentRules.InvalidFields(request, Now, CustomerPaymentKind.Payment, mode, forOrder: true);

    [Fact]
    public void Cash_for_an_order_needs_nothing_more() => Assert.Empty(Invalid(Request()));

    [Fact]
    public void A_transfer_for_an_order_needs_its_bank_reference_like_any_money_that_arrived()
    {
        Assert.Contains("Bank reference", Invalid(Request(method: CustomerPaymentMethod.OtherTransfer)));
        Assert.Empty(Invalid(Request(method: CustomerPaymentMethod.OtherTransfer, bankReference: "FT26316ABC")));
    }

    [Fact]
    public void An_advance_has_no_discount_and_names_no_invoice()
    {
        Assert.Contains("Discount amount", Invalid(Request(discount: 10_000m)));
        Assert.Contains("Allocations", Invalid(Request(allocations: [new CustomerPaymentAllocationInput(Guid.NewGuid(), 100_000m)])));
        // The same payment for no order may still carry both.
        Assert.DoesNotContain("Discount amount", CustomerPaymentRules.InvalidFields(Request(discount: 10_000m), Now));
    }

    [Fact]
    public void An_advance_is_money_above_zero_by_cash_or_transfer()
    {
        Assert.Contains("Amount", Invalid(Request(amount: 0m)));
        Assert.Contains("Amount", Invalid(Request(amount: null)));
        Assert.Contains("Method", Invalid(Request(method: CustomerPaymentMethod.EWallet)));
        Assert.Contains("Method", Invalid(Request(method: null)));
    }

    [Fact]
    public void A_vietqr_advance_waits_for_its_transfer_and_needs_the_bank_account()
    {
        Assert.Contains("Cash account", Invalid(Request(), CustomerPaymentMode.WaitsForTransfer));
        Assert.Empty(Invalid(Request(cashAccount: Guid.NewGuid()), CustomerPaymentMode.WaitsForTransfer));
        Assert.Contains("Discount amount", Invalid(Request(discount: 5_000m, cashAccount: Guid.NewGuid()), CustomerPaymentMode.WaitsForTransfer));
    }

    // ---- The statement ----

    [Fact]
    public void The_statement_shows_an_advance_with_its_order_and_whether_the_order_still_holds_it()
    {
        var held = new CustomerLedgerPayment(
            Customer, Guid.NewGuid(), "PT000001", CustomerPaymentKind.Payment, new DateOnly(2026, 11, 12), 500_000m, CustomerPaymentMethod.Cash, null, null,
            StatusIds.CustomerPayment.Completed, PaidAt: Now, SalesOrderId: Guid.NewGuid(), SalesOrderCode: "DH000001", OrderHoldsAdvance: true);
        var released = held with { Id = Guid.NewGuid(), Code = "PT000002", OrderHoldsAdvance = false };
        var plain = held with { Id = Guid.NewGuid(), Code = "PT000003", SalesOrderId = null, SalesOrderCode = null, OrderHoldsAdvance = false };

        var lines = CustomerStatementLedger.Build([], [held, released, plain], DateOnly.MinValue, DateOnly.MaxValue);

        Assert.Equal(
            [("PT000001", "DH000001", true), ("PT000002", "DH000001", false), ("PT000003", null, false)],
            lines.Entries.Select(e => (e.Code, e.SalesOrderCode, e.ReservedForOrder)));
        // The advance still lowers the debt as any payment does.
        Assert.Equal(-1_500_000m, lines.ClosingDebt);
    }

    [Fact]
    public void An_advance_that_is_cancelled_or_waits_for_its_transfer_is_never_reserved()
    {
        var order = Guid.NewGuid();
        CustomerLedgerPayment Of(int status) => new(
            Customer, Guid.NewGuid(), "PT1", CustomerPaymentKind.Payment, new DateOnly(2026, 11, 12), 100_000m, CustomerPaymentMethod.Cash, null, null, status, SalesOrderId: order,
            OrderHoldsAdvance: true);

        Assert.True(Of(StatusIds.CustomerPayment.Completed).Reserved);
        Assert.True(Of(StatusIds.CustomerPayment.AwaitingConfirmation).Reserved);
        Assert.False(Of(StatusIds.CustomerPayment.Cancelled).Reserved);
        Assert.False(Of(StatusIds.CustomerPayment.PendingTransfer).Reserved);
    }

    // ---- Purchase requests ----

    [Theory]
    [InlineData(StatusIds.PurchaseRequest.AwaitingResponse, true)]
    [InlineData(StatusIds.PurchaseRequest.Accepted, true)]
    [InlineData(StatusIds.PurchaseRequest.Declined, false)]
    [InlineData(StatusIds.PurchaseRequest.Cancelled, false)]
    [InlineData(StatusIds.PurchaseRequest.Ordered, false)]
    public void Only_a_waiting_or_accepted_request_allows_an_order(int status, bool allowed) => Assert.Equal(allowed, PurchaseRequestRules.AllowsOrder(status));

    [Theory]
    [InlineData(StatusIds.PurchaseRequest.Ordered)]
    [InlineData(StatusIds.PurchaseRequest.Declined)]
    [InlineData(StatusIds.PurchaseRequest.Cancelled)]
    public void An_ordered_declined_or_cancelled_request_can_no_longer_be_cancelled_or_answered(int status)
    {
        Assert.False(PurchaseRequestRules.AllowsCancel(status));
        Assert.False(PurchaseRequestRules.AllowsResponse(status));
    }
}
