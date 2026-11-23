using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.Reports.EndOfDay;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Reports;

/// <summary>The repeated enum parameters of the end-of-day report: names in any letter case, anything else names the field as invalid.</summary>
public class EndOfDayParsingTests
{
    [Fact]
    public void Names_are_read_in_any_letter_case_and_repeats_count_once()
    {
        var invalid = new List<string>();

        var methods = EndOfDayService.ParseEnums<CustomerPaymentMethod>(["cash", " EWallet ", "Cash"], "paymentMethods", invalid);

        Assert.Equal([CustomerPaymentMethod.Cash, CustomerPaymentMethod.EWallet], methods);
        Assert.Empty(invalid);
    }

    [Theory]
    [InlineData("Cheque")]
    [InlineData("7")]
    [InlineData("1")]
    public void A_value_that_is_not_a_name_names_the_field(string value)
    {
        var invalid = new List<string>();

        var channels = EndOfDayService.ParseEnums<SalesOrderSource>(["App", value], "channels", invalid);

        Assert.Equal(["channels"], invalid);
        Assert.Equal([SalesOrderSource.App], channels);
    }

    [Fact]
    public void Nothing_given_is_no_limit()
    {
        var invalid = new List<string>();

        Assert.Empty(EndOfDayService.ParseEnums<CashSource>(null, "sources", invalid));
        Assert.Empty(EndOfDayService.ParseEnums<CashSource>(["", "  "], "sources", invalid));
        Assert.Empty(invalid);
    }

    [Fact]
    public void The_cash_book_sources_and_kinds_use_the_names_of_the_documented_values()
    {
        var invalid = new List<string>();

        var sources = EndOfDayService.ParseEnums<CashSource>(["CustomerPayment", "CustomerPaidBack", "SupplierPayment", "SupplierPaidBack", "CashVoucher"], "sources", invalid);
        var kinds = EndOfDayService.ParseEnums<CashPartyKind>(["Customer", "Supplier", "Employee", "Other"], "partyKinds", invalid);
        var funds = EndOfDayService.ParseEnums<CashFund>(["Cash", "Bank", "EWallet"], "funds", invalid);

        Assert.Empty(invalid);
        Assert.Equal((5, 4, 3), (sources.Count, kinds.Count, funds.Count));
    }
}
