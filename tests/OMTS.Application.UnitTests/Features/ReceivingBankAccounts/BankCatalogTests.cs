using OMTS.Application.Features.ReceivingBankAccounts;

namespace OMTS.Application.UnitTests.Features.ReceivingBankAccounts;

public sealed class BankCatalogTests
{
    [Fact]
    public void Every_bank_has_a_six_digit_bin_that_only_it_has_and_a_name()
    {
        Assert.All(BankCatalog.All, bank =>
        {
            Assert.Matches(@"\A[0-9]{6}\z", bank.Bin);
            Assert.False(string.IsNullOrWhiteSpace(bank.ShortName));
            Assert.False(string.IsNullOrWhiteSpace(bank.Name));
        });
        Assert.Equal(BankCatalog.All.Count, BankCatalog.All.Select(b => b.Bin).Distinct().Count());
        Assert.Equal(BankCatalog.All.Count, BankCatalog.All.Select(b => b.ShortName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_list_is_in_the_order_of_the_short_names() =>
        Assert.Equal(BankCatalog.All.Select(b => b.ShortName).Order(StringComparer.OrdinalIgnoreCase), BankCatalog.All.Select(b => b.ShortName));

    [Fact]
    public void Wallets_are_not_banks() =>
        Assert.DoesNotContain(BankCatalog.All, b => b.ShortName is "MoMo" or "ZALOPAY" or "ViettelMoney" or "VNPTMoney" or "MVAS" or "MAFC");

    [Theory]
    [InlineData("970436", "Vietcombank")]
    [InlineData("970418", "BIDV")]
    [InlineData("970405", "Agribank")]
    public void A_bank_is_found_by_its_bin(string bin, string shortName) => Assert.Equal(shortName, BankCatalog.FindByBin(bin)?.ShortName);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("000000")]
    public void An_unknown_bin_finds_nothing(string? bin) => Assert.Null(BankCatalog.FindByBin(bin));
}
