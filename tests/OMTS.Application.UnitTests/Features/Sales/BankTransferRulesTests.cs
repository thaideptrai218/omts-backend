using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Sales.Payments;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>GFR-08 as pure rules: the invoice code of a transfer content, and the transfers that are not recorded.</summary>
public class BankTransferRulesTests
{
    private const string Account = "999900000001";

    [Theory]
    [InlineData("HD000123", "HD000123")]
    [InlineData("hd000123", "HD000123")]
    [InlineData("MBVCB.1234567.HD000123.CT tu DEMO", "HD000123")]
    [InlineData("Thanh toan HD-000123", "HD000123")]
    [InlineData("HD 000123 tien hau", "HD000123")]
    [InlineData("0123456HD000123", "HD000123")]
    [InlineData("HD1000000", "HD1000000")]
    [InlineData("HD000123 HD000124", "HD000123")]
    [InlineData("CHD000123", null)]
    [InlineData("HD00012", null)]
    [InlineData("Tien hang thang 10", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_invoice_code_is_read_from_the_content_as_bank_apps_send_it(string? content, string? expected) =>
        Assert.Equal(expected, BankTransferRules.InvoiceCodeIn(content));

    [Fact]
    public void Only_incoming_transfers_to_the_receiving_account_with_an_invoice_code_are_recorded()
    {
        var notice = new BankTransferNotice("FT-DEMO-1", Account, 8_000_000m, "HD000123", DateTimeOffset.UtcNow, BankTransferDirection.In);

        Assert.Null(BankTransferRules.Ignored(notice, Account));
        Assert.Null(BankTransferRules.Ignored(notice with { AccountNumber = " 9999 0000 0001 " }, Account));
        Assert.Equal(BankTransferIgnoredReason.Outgoing, BankTransferRules.Ignored(notice with { Direction = BankTransferDirection.Out }, Account));
        Assert.Equal(BankTransferIgnoredReason.NotTheReceivingAccount, BankTransferRules.Ignored(notice with { AccountNumber = "999900000002" }, Account));
        Assert.Equal(BankTransferIgnoredReason.NotTheReceivingAccount, BankTransferRules.Ignored(notice, receivingAccountNumber: null));
        Assert.Equal(BankTransferIgnoredReason.NoInvoiceCode, BankTransferRules.Ignored(notice with { Content = "chuyen tien" }, Account));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { BankReference = " " }, Account));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { BankReference = new string('F', 101) }, Account));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { Amount = 0m }, Account));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { Amount = 1000.5m }, Account));
    }

    // API-13 never fails on a transfer time: one no date can be made of, or far outside a bank's clock, is a malformed field like the others.
    [Fact]
    public void A_transfer_time_outside_what_a_bank_can_report_is_invalid()
    {
        var now = new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);
        var notice = new BankTransferNotice("FT-DEMO-2", Account, 300_000m, "HD000123", now, BankTransferDirection.In);

        Assert.Null(BankTransferRules.Ignored(notice with { OccurredAt = now.AddHours(23) }, Account, now));
        Assert.Null(BankTransferRules.Ignored(notice with { OccurredAt = BankTransferRules.EarliestTransferTime }, Account, now));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { OccurredAt = now.AddDays(2) }, Account, now));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { OccurredAt = BankTransferRules.EarliestTransferTime.AddTicks(-1) }, Account, now));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { OccurredAt = DateTimeOffset.MaxValue }, Account));
    }

    // MSG-SALE-06 for SALE-05 step 8: the transfer first, then the cash part (AC-07); MSG-SALE-14 names the code twice.
    [Fact]
    public void The_texts_of_a_vietqr_sale_name_the_transfer_and_the_stopped_code()
    {
        Assert.Equal(
            "Invoice HD000123 is completed: 8,000,000 VND received by VietQR; 4,000,000 VND collected in cash.",
            UserMessages.SaleCompleted("HD000123", 4_000_000m, 0m, "Bà Lan", vietQrAmount: 8_000_000m).Text);
        Assert.Equal("Invoice HD000124 is completed: 300,000 VND received by VietQR.", UserMessages.SaleCompleted("HD000124", 0m, 0m, "a walk-in customer", 300_000m).Text);
        Assert.Equal("Invoice HD000125 is completed: 0 VND collected in cash.", UserMessages.SaleCompleted("HD000125", 0m, 0m, "a walk-in customer").Text);

        var stop = UserMessages.StopSaleQuestion("HD000123");
        Assert.Equal((MessageCodes.Sale14, "HD000123"), (stop.Code, stop.Parameters!["invoice_code"]));
        Assert.StartsWith("The transfer for invoice HD000123 has not arrived. Stop this sale? Nothing is saved, and code HD000123 is kept in the audit log.", stop.Text);
        Assert.Equal(MessageCodes.Sale13, UserMessages.VietQrNeedsReceivingAccount.Code);
    }
}
