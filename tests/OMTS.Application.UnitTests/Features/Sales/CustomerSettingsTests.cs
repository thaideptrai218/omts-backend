using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>The debt limit of the customer settings and the profile fields the cooperative makes required.</summary>
public class CustomerSettingsTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);
    private static readonly CustomerRequiredFields None = new(false, false, false, false, false, false, false, false);

    private static SaveCustomerRequest Request(
        string? phone2 = null, string? email = null, Gender? gender = null, DateOnly? birth = null, string? address = null, string? province = null, string? ward = null,
        string? facebook = null, string? bin = null, string? account = null, string? tax = null) =>
        new("An", "0901234567", address, null, PhoneNumber2: phone2, Email: email, Gender: gender, BirthDate: birth, Province: province, Ward: ward, Facebook: facebook,
            TaxCode: tax, BankBin: bin, BankAccountNumber: account);

    [Fact]
    public void A_warning_that_is_off_flags_nobody()
    {
        var policy = new CustomerDebtPolicy(false, 500_000m, 15, true);

        var standing = policy.Evaluate(9_000_000m, 90);

        Assert.False(standing.Over);
        Assert.Equal(90, standing.DebtDays);
    }

    [Theory]
    [InlineData(500_000, 3, false, false)]
    [InlineData(500_001, 3, true, false)]
    [InlineData(100_000, 15, false, false)]
    [InlineData(100_000, 16, false, true)]
    [InlineData(600_000, 20, true, true)]
    public void A_customer_is_over_when_the_debt_is_above_the_amount_or_older_than_the_days(decimal debt, int days, bool overAmount, bool overDays)
    {
        var standing = new CustomerDebtPolicy(true, 500_000m, 15, true).Evaluate(debt, days);

        Assert.Equal(overAmount, standing.OverAmount);
        Assert.Equal(overDays, standing.OverDays);
        Assert.Equal(overAmount || overDays, standing.Over);
    }

    [Fact]
    public void A_limit_that_is_not_set_never_flags()
    {
        var onlyDays = new CustomerDebtPolicy(true, null, 15, true).Evaluate(900_000_000m, 3);
        var onlyAmount = new CustomerDebtPolicy(true, 500_000m, null, true).Evaluate(100_000m, 400);

        Assert.False(onlyDays.Over);
        Assert.False(onlyAmount.Over);
    }

    [Fact]
    public void Money_owed_back_or_no_debt_is_never_over_the_limit_and_has_no_age()
    {
        var standing = new CustomerDebtPolicy(true, 500_000m, 15, true).Evaluate(-2_000_000m, 40);

        Assert.False(standing.Over);
        Assert.Null(standing.DebtDays);
    }

    [Fact]
    public void The_refusal_message_has_the_customer_code_and_says_which_limit()
    {
        var message = UserMessages.CustomerOverDebtLimit("Nguyen Van An", 700_000m, 20, overAmount: true, overDays: false);

        Assert.Equal(MessageCodes.Customer01, message.Code);
        Assert.Contains("Nguyen Van An", message.Text);
        Assert.Contains("700,000", message.Text);
        Assert.Contains("hạn mức số tiền nợ", message.Text);
        Assert.DoesNotContain("số ngày nợ", message.Text);
    }

    [Fact]
    public void Only_the_name_and_the_first_phone_number_are_required_by_default()
    {
        var (_, invalid) = CustomerEntries.From(Request(), phoneLocked: false, Today, None);

        Assert.Empty(invalid);
    }

    [Fact]
    public void Every_required_field_that_is_missing_is_named()
    {
        var all = new CustomerRequiredFields(true, true, true, true, true, true, true, true);

        var (_, invalid) = CustomerEntries.From(Request(), phoneLocked: false, Today, all);

        Assert.Equal(
            ["Second phone number", "Email", "Gender", "Birth date", "Address", "Province", "Ward", "Facebook", "Bank", "Bank account number", "Tax code"],
            invalid);
    }

    [Fact]
    public void A_filled_required_field_is_accepted()
    {
        var required = new CustomerRequiredFields(true, true, true, true, true, true, false, true);

        var (entries, invalid) = CustomerEntries.From(
            Request("0907654321", "an@example.com", Gender.Male, new DateOnly(1990, 5, 1), "1 Le Loi", "Khanh Hoa", "Van Ninh", "fb.com/an", tax: "0312345678"),
            phoneLocked: false, Today, required);

        Assert.Empty(invalid);
        Assert.Equal("0312345678", entries.TaxCode);
    }

    [Fact]
    public void A_required_field_that_is_also_malformed_is_named_once()
    {
        var required = new CustomerRequiredFields(false, true, false, false, false, false, false, false);

        var (_, invalid) = CustomerEntries.From(Request(email: "not an email"), phoneLocked: false, Today, required);

        Assert.Equal(["Email"], invalid);
    }

    [Fact]
    public void A_profile_linked_to_a_trader_account_still_needs_the_required_fields()
    {
        var required = new CustomerRequiredFields(false, true, false, false, false, false, false, false);

        var (_, invalid) = CustomerEntries.From(Request() with { PhoneNumber = null }, phoneLocked: true, Today, required);

        Assert.Equal(["Email"], invalid);
    }
}
