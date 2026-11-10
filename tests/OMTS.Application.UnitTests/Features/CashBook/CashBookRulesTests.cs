using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.CashBook;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.CashBook;

public class CashBookRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 10, 0, 0, TimeSpan.FromHours(7));

    private static readonly CashVoucherType ManualReceiptType = new() { Direction = CashDirection.Receipt, Category = CashCategory.OtherIncome, Manual = true, IsActive = true };
    private static readonly CashAccount BankAccount = new() { Kind = CashAccountKind.Bank, IsActive = true };
    private static readonly CashAccount Wallet = new() { Kind = CashAccountKind.EWallet, IsActive = true };

    private static SaveCashVoucherRequest Valid(CashFund fund = CashFund.Cash, Guid? accountId = null) => new(
        Code: null, CashDirection.Receipt, fund, accountId, ManualReceiptType.Id, Now.AddHours(-1), 150_000);

    [Theory]
    [InlineData(CashFund.Cash, CashDirection.Receipt, "TTM000007")]
    [InlineData(CashFund.Cash, CashDirection.Payment, "CTM000007")]
    [InlineData(CashFund.Bank, CashDirection.Receipt, "TNH000007")]
    [InlineData(CashFund.Bank, CashDirection.Payment, "CNH000007")]
    [InlineData(CashFund.EWallet, CashDirection.Receipt, "TVD000007")]
    [InlineData(CashFund.EWallet, CashDirection.Payment, "CVD000007")]
    public void A_generated_code_has_the_prefix_of_its_fund_and_direction(CashFund fund, CashDirection direction, string expected) =>
        Assert.Equal(expected, CashBookRules.GeneratedCode(fund, direction, 7));

    [Fact]
    public void A_code_grows_past_six_digits_instead_of_wrapping() =>
        Assert.Equal("TTM1000000", CashBookRules.GeneratedCode(CashFund.Cash, CashDirection.Receipt, 1_000_000));

    [Fact]
    public async Task An_empty_code_is_generated_and_skips_a_number_someone_typed()
    {
        var service = new CashVoucherCodeService(new CountingNumbers(), new Vouchers("ttm000001", "TTM000002"));

        var (code, error) = await service.ForNewAsync(CashFund.Cash, CashDirection.Receipt, "  ", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("TTM000003", code);
    }

    [Fact]
    public async Task A_typed_code_is_kept_as_typed_and_refused_when_another_voucher_has_it_in_any_case()
    {
        var service = new CashVoucherCodeService(new CountingNumbers(), new Vouchers("PHIEU-1"));

        var (kept, noError) = await service.ForNewAsync(CashFund.Bank, CashDirection.Payment, " Phieu-2 ", CancellationToken.None);
        var (_, duplicate) = await service.ForNewAsync(CashFund.Bank, CashDirection.Payment, "phieu-1", CancellationToken.None);

        Assert.Null(noError);
        Assert.Equal("Phieu-2", kept);
        Assert.NotNull(duplicate);
        Assert.Equal("Code", duplicate.Details!["field"]);
    }

    [Fact]
    public void A_valid_cash_voucher_has_no_invalid_field() =>
        Assert.Empty(CashBookRules.InvalidVoucher(Valid(), ManualReceiptType, null, Now));

    [Fact]
    public void A_cash_voucher_names_no_account_and_a_bank_voucher_an_account_of_its_kind()
    {
        var bankId = BankAccount.Id;
        Assert.Equal(["Cash account"], CashBookRules.InvalidVoucher(Valid(CashFund.Cash, bankId), ManualReceiptType, BankAccount, Now));
        Assert.Equal(["Cash account"], CashBookRules.InvalidVoucher(Valid(CashFund.Bank), ManualReceiptType, null, Now));
        Assert.Equal(["Cash account"], CashBookRules.InvalidVoucher(Valid(CashFund.Bank, Wallet.Id), ManualReceiptType, Wallet, Now));
        Assert.Empty(CashBookRules.InvalidVoucher(Valid(CashFund.Bank, bankId), ManualReceiptType, BankAccount, Now));
        Assert.Empty(CashBookRules.InvalidVoucher(Valid(CashFund.EWallet, Wallet.Id), ManualReceiptType, Wallet, Now));
    }

    [Fact]
    public void An_account_out_of_use_is_refused_unless_the_voucher_already_has_it()
    {
        var retired = new CashAccount { Kind = CashAccountKind.Bank, IsActive = false };
        var request = Valid(CashFund.Bank, retired.Id);

        Assert.Equal(["Cash account"], CashBookRules.InvalidVoucher(request, ManualReceiptType, retired, Now));
        Assert.Empty(CashBookRules.InvalidVoucher(request, ManualReceiptType, retired, Now, storedAccountId: retired.Id));
    }

    [Fact]
    public void The_type_must_be_chosen_by_hand_and_of_the_direction_of_the_voucher()
    {
        var automatic = new CashVoucherType { Direction = CashDirection.Receipt, Category = CashCategory.Goods, Manual = false, IsActive = true };
        var expense = new CashVoucherType { Direction = CashDirection.Payment, Category = CashCategory.OtherExpense, Manual = true, IsActive = true };

        Assert.Equal(["Type"], CashBookRules.InvalidVoucher(Valid() with { TypeId = automatic.Id }, automatic, null, Now));
        Assert.Equal(["Type"], CashBookRules.InvalidVoucher(Valid() with { TypeId = expense.Id }, expense, null, Now));
        Assert.Equal(["Type"], CashBookRules.InvalidVoucher(Valid(), null, null, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1000.5)]
    public void The_amount_is_whole_dong_above_zero(double amount) =>
        Assert.Equal(["Amount"], CashBookRules.InvalidVoucher(Valid() with { Amount = (decimal)amount }, ManualReceiptType, null, Now));

    [Fact]
    public void The_time_may_not_be_in_the_future_beyond_clock_tolerance()
    {
        Assert.Equal(["Occurred at"], CashBookRules.InvalidVoucher(Valid() with { OccurredAt = Now.AddHours(1) }, ManualReceiptType, null, Now));
        Assert.Empty(CashBookRules.InvalidVoucher(Valid() with { OccurredAt = Now.AddMinutes(2) }, ManualReceiptType, null, Now));
    }

    [Fact]
    public void A_party_needs_both_its_kind_and_its_id() =>
        Assert.Equal(["Party"], CashBookRules.InvalidVoucher(Valid() with { PartyKind = CashPartyKind.Customer }, ManualReceiptType, null, Now));

    [Fact]
    public void A_user_type_is_one_of_the_categories_a_user_may_make()
    {
        Assert.Empty(CashBookRules.InvalidType(new(CashDirection.Receipt, CashCategory.TransferWithdrawal, "Rút tiền")));
        Assert.Equal(["Category"], CashBookRules.InvalidType(new(CashDirection.Receipt, CashCategory.Goods, "Thu hàng")));
        Assert.Equal(["Category"], CashBookRules.InvalidType(new(CashDirection.Payment, CashCategory.Payroll, "Lương")));
        Assert.Equal(["Name"], CashBookRules.InvalidType(new(CashDirection.Payment, CashCategory.OtherExpense, " ")));
    }

    [Fact]
    public void The_summary_opens_with_what_the_earlier_lines_leave_and_closes_with_the_period()
    {
        var summary = CashBookSummary.Of(receiptsBefore: 500_000, paymentsBefore: 200_000, receipts: 120_000, payments: 70_000);

        Assert.Equal(new CashBookSummary(300_000, 120_000, 70_000, 350_000), summary);
    }

    [Fact]
    public void Without_earlier_lines_the_opening_balance_is_zero() =>
        Assert.Equal(new CashBookSummary(0, 10, 25, -15), CashBookSummary.Of(0, 0, 10, 25));

    private sealed class CountingNumbers : IDocumentNumberAllocator
    {
        private long last;

        public Task<long> NextAsync(string key, CancellationToken cancellationToken) => Task.FromResult(++last);
    }

    private sealed class Vouchers(params string[] codes) : ICashVoucherRepository
    {
        public Task<Guid?> FindIdByCodeAsync(string code, Guid? exceptVoucherId, CancellationToken cancellationToken) =>
            Task.FromResult(codes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) ? (Guid?)Guid.NewGuid() : null);

        public Task<CashVoucher?> GetForEditAsync(Guid voucherId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(CashVoucher voucher) => throw new NotSupportedException();
    }
}
