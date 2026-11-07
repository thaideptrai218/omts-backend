using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Sales.Debts;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>BR-DEBT-02: customer debt = invoices − sales returns − payments received + money paid back; below zero it is money owed back.</summary>
public class CustomerDebtMathTests
{
    [Fact]
    public void The_debt_is_the_formula_of_br_debt_02()
    {
        Assert.Equal(0m, CustomerDebtMath.Debt(CustomerDebtFigures.None));
        Assert.Equal(8_000_000m, CustomerDebtMath.Debt(new CustomerDebtFigures(12_000_000m, 0m, 4_000_000m, 0m))); // BR-SALE-07: 4M cash, 8M on the debt
        Assert.Equal(-500_000m, CustomerDebtMath.Debt(new CustomerDebtFigures(9_500_000m, 0m, 10_000_000m, 0m))); // AC-08: a price cut on a paid invoice
        Assert.Equal(0m, CustomerDebtMath.Debt(new CustomerDebtFigures(9_500_000m, 0m, 10_000_000m, 500_000m))); // paid back
        Assert.Equal(1_000_000m, CustomerDebtMath.Debt(new CustomerDebtFigures(10_000_000m, 9_000_000m, 0m, 0m))); // a sales return
    }

    [Fact]
    public void A_debt_is_either_a_receivable_or_money_owed_back()
    {
        Assert.Equal((8_000_000m, 0m), (CustomerDebtMath.Receivable(8_000_000m), CustomerDebtMath.OwedBack(8_000_000m)));
        Assert.Equal((0m, 500_000m), (CustomerDebtMath.Receivable(-500_000m), CustomerDebtMath.OwedBack(-500_000m)));
        Assert.Equal((0m, 0m), (CustomerDebtMath.Receivable(0m), CustomerDebtMath.OwedBack(0m)));
    }

    [Fact]
    public void An_invoice_adds_its_total_and_its_payments_take_off_what_they_bring()
    {
        Assert.Equal(13_000_000m, CustomerDebtMath.AfterInvoice(5_000_000m, 12_000_000m, 4_000_000m));
        // Cash above the total that lowered the debt: 6,300,000 recorded for a 6,000,000 invoice.
        Assert.Equal(0m, CustomerDebtMath.AfterInvoice(300_000m, 6_000_000m, 6_300_000m));
    }

    // MSG-SALE-06 and the {price_text} of MSG-SALE-08, as the SRS words them.
    [Fact]
    public void The_sale_messages_name_the_payments_and_the_prices_the_owner_decided()
    {
        Assert.Equal("Invoice HD000123 is completed: 4,000,000 VND collected in cash; 8,000,000 VND added to the debt of Anh Tùng.",
            UserMessages.SaleCompleted("HD000123", 4_000_000m, 8_000_000m, "Anh Tùng").Text);
        Assert.Equal("Invoice HD000124 is completed: 6,000,000 VND collected in cash.", UserMessages.SaleCompleted("HD000124", 6_000_000m, 0m, "a walk-in customer").Text);
        Assert.Equal("Invoice HD000125 is completed: 12,000,000 VND added to the debt of Anh Tùng.", UserMessages.SaleCompleted("HD000125", 0m, 12_000_000m, "Anh Tùng").Text);

        Assert.Equal(" Price decided by the Cooperative Owner: 55,000 VND/kg (list price 60,000); discount 200,000 VND.", UserMessages.SalePriceText([(55_000m, 60_000m)], 200_000m));
        Assert.Equal(" Discount 200,000 VND.", UserMessages.SalePriceText([], 200_000m));
        Assert.Equal(string.Empty, UserMessages.SalePriceText([], 0m));
        Assert.Equal(
            "Bà Lan sold 200 kg to a walk-in customer for 6,000,000 VND (invoice HD000123, Pay now).",
            UserMessages.SaleNotice("Bà Lan", 200m, "a walk-in customer", 6_000_000m, "HD000123", "Pay now", string.Empty).Text);
    }
}
