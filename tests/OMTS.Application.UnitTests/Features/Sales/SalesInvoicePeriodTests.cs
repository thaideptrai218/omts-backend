using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>SALE-09 steps 1 and 3 (the period) and BR-SALE-08 (whether an invoice is paid).</summary>
public class SalesInvoicePeriodTests
{
    private static readonly DateOnly Today = new(2026, 11, 9);

    [Fact]
    public void No_period_is_today() =>
        Assert.Equal((Today, Today), SalesInvoicePeriod.Resolve(null, null, Today).Value);

    [Fact]
    public void Only_a_start_runs_to_today_and_only_an_end_is_that_one_day()
    {
        Assert.Equal((Today.AddDays(-3), Today), SalesInvoicePeriod.Resolve(Today.AddDays(-3), null, Today).Value);
        Assert.Equal((Today.AddDays(-3), Today.AddDays(-3)), SalesInvoicePeriod.Resolve(null, Today.AddDays(-3), Today).Value);
    }

    // SALE-09.0.E1.
    [Fact]
    public void A_start_after_the_end_is_refused_and_so_is_a_start_in_the_future_without_an_end()
    {
        Assert.Equal("MSG-GEN-01", SalesInvoicePeriod.Resolve(Today, Today.AddDays(-1), Today).Error!.Message.Code);
        Assert.Equal("MSG-GEN-01", SalesInvoicePeriod.Resolve(Today.AddDays(1), null, Today).Error!.Message.Code);
    }

    [Fact]
    public void A_period_is_at_most_five_years() =>
        Assert.Equal("MSG-GEN-01", SalesInvoicePeriod.Resolve(Today.AddYears(-5).AddDays(-1), Today, Today).Error!.Message.Code);

    private static readonly Guid Invoice = Guid.NewGuid();

    [Fact]
    public void A_cancelled_invoice_has_no_payment_state()
    {
        var state = SalesInvoicePaidState.Of(Invoice, StatusIds.SalesInvoice.Cancelled, 500m, Guid.NewGuid(), 0m, null);
        Assert.Equal(new SalesInvoicePaidState(null, 0m, 0m), state);
    }

    // BR-DEBT-05 through the allocator: the part still unpaid, whatever the invoice's own payments.
    [Fact]
    public void An_invoice_with_a_profile_follows_the_allocation_of_the_debt()
    {
        var allocation = new CustomerDebtAllocation(300m, [new UnpaidInvoice(Invoice, "HD1", Today, 500m, 200m, 300m, 2)], Today, 2);

        Assert.Equal(new SalesInvoicePaidState(false, 200m, 300m), SalesInvoicePaidState.Of(Invoice, StatusIds.SalesInvoice.Completed, 500m, Guid.NewGuid(), 500m, allocation));
        Assert.Equal(new SalesInvoicePaidState(true, 500m, 0m), SalesInvoicePaidState.Of(Guid.NewGuid(), StatusIds.SalesInvoice.Completed, 500m, Guid.NewGuid(), 0m, allocation));
    }

    // BR-SALE-07: a walk-in invoice counts in no debt, so only its own payments cover it.
    [Theory]
    [InlineData(500, 500, true, 500, 0)]
    [InlineData(500, 400, false, 400, 100)]
    [InlineData(500, 700, true, 500, 0)]
    [InlineData(500, 0, false, 0, 500)]
    public void A_walk_in_invoice_is_covered_by_its_own_payments(decimal total, decimal own, bool paid, decimal covered, decimal unpaid) =>
        Assert.Equal(new SalesInvoicePaidState(paid, covered, unpaid), SalesInvoicePaidState.Of(Invoice, StatusIds.SalesInvoice.Completed, total, null, own, null));
}
