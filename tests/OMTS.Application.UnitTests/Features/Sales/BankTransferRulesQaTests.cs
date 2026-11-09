using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Features.Sales.Payments;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>QA of GFR-08 as pure rules (PR 7): the invoice code at the edges of the transfer content, and the amounts at the edges of what is recorded.</summary>
public class BankTransferRulesQaTests
{
    private const string Account = "999900000001";

    [Theory]
    [InlineData("HD000123456789", "HD000123456789")] // 12 digits: the longest code read
    [InlineData("HD0001234567890", null)] // 13 digits: not a code, and not cut to 12
    [InlineData("hd_000123", "HD000123")]
    [InlineData("HD.000123", "HD000123")]
    [InlineData("HD--000123", null)] // one separator only
    [InlineData("1HD000123", "HD000123")] // a digit before HD is a bank prefix, not a word
    [InlineData("HD000123abc", "HD000123")]
    [InlineData("IBFT HD000123 HD000999", "HD000123")] // several codes: the first one
    [InlineData("THANHTOANHD000123", null)] // letters glued before HD: another word (see QA L)
    [InlineData("HD 000123", "HD000123")]
    [InlineData("HD\t000123", "HD000123")]
    public void The_invoice_code_at_the_edges_of_the_content(string content, string? expected) =>
        Assert.Equal(expected, BankTransferRules.InvoiceCodeIn(content));

    [Fact]
    public void Amounts_at_the_edges_and_references_with_spaces()
    {
        var notice = new BankTransferNotice("FT-DEMO-1", Account, 1m, "HD000123", DateTimeOffset.UtcNow, BankTransferDirection.In);

        Assert.Null(BankTransferRules.Ignored(notice, Account));
        Assert.Null(BankTransferRules.Ignored(notice with { Amount = 999_999_999_999_999_999m }, Account));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { Amount = 1_000_000_000_000_000_000m }, Account));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { Amount = -1m }, Account));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { Amount = 0.5m }, Account));
        Assert.Null(BankTransferRules.Ignored(notice with { Amount = 1.000m }, Account));
        // 100 characters after the spaces are trimmed: accepted; 101: refused.
        Assert.Null(BankTransferRules.Ignored(notice with { BankReference = "  " + new string('F', BankTransferRules.MaxBankReferenceLength) + "  " }, Account));
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { AccountNumber = " " }, Account));
        // An invalid transfer is Invalid before it is Outgoing: nothing about it is trusted.
        Assert.Equal(BankTransferIgnoredReason.Invalid, BankTransferRules.Ignored(notice with { Amount = 0m, Direction = BankTransferDirection.Out }, Account));
        Assert.True(BankTransferRules.SameAccount("9999 0000 0001", "999900000001"));
        Assert.False(BankTransferRules.SameAccount("999900000001", "99990000001"));
    }
}
