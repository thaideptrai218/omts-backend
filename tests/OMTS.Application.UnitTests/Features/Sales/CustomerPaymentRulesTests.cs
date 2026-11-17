using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>BR-DEBT-03 checks of DEBT-02, its questions MSG-DEBT-04/05, the refusal MSG-DEBT-06, the Owner's notice MSG-DEBT-03 and the cancel date in the statement (BR-DEBT-04).</summary>
public class CustomerPaymentRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.FromHours(7));

    private static RecordCustomerPaymentRequest Transfer(decimal? amount = 100_000m, DateOnly? date = null, string? reference = "FT-DEMO-1") =>
        new(Guid.NewGuid(), date ?? Today, amount, CustomerPaymentMethod.OtherTransfer, reference);

    [Fact]
    public void A_payment_needs_a_date_not_after_today_a_whole_amount_above_zero_cash_or_another_transfer_and_the_reference_of_a_transfer()
    {
        Assert.Empty(CustomerPaymentRules.InvalidFields(Transfer(), Now));
        Assert.Empty(CustomerPaymentRules.InvalidFields(Transfer() with { Method = CustomerPaymentMethod.Cash, BankReference = null }, Now));
        Assert.Equal(["Payment date"], CustomerPaymentRules.InvalidFields(Transfer(date: Today.AddDays(1)), Now));
        Assert.Equal(["Amount"], CustomerPaymentRules.InvalidFields(Transfer(amount: 0m), Now));
        Assert.Equal(["Amount"], CustomerPaymentRules.InvalidFields(Transfer(amount: 100.5m), Now));
        Assert.Equal(["Bank reference"], CustomerPaymentRules.InvalidFields(Transfer(reference: "  "), Now));
        Assert.Equal(["Bank reference"], CustomerPaymentRules.InvalidFields(Transfer(reference: new string('9', 101)), Now));
        // The VietQR code of an invoice is recorded by the system only (GFR-08).
        Assert.Equal(["Method"], CustomerPaymentRules.InvalidFields(Transfer() with { Method = CustomerPaymentMethod.VietQrInvoice }, Now));
        Assert.Equal(["Payment date", "Amount", "Method", "Note", "Deduplication key"], CustomerPaymentRules.InvalidFields(
            new RecordCustomerPaymentRequest(null, null, null, null, Note: new string('n', 1001), DeduplicationKey: new string('k', 101)), Now));
        Assert.Null(CustomerPaymentRules.Reason("   "));
        Assert.Equal("Never arrived", CustomerPaymentRules.Reason("  Never arrived "));
    }

    // DEBT-02.1: asked when the amount is above the debt or the debt is 0 or below; never when it fits the debt.
    [Theory]
    [InlineData(1_000_000, 400_000, false)]
    [InlineData(1_000_000, 1_000_000, false)]
    [InlineData(1_000_000, 1_200_000, true)]
    [InlineData(0, 100_000, true)]
    [InlineData(-50_000, 100_000, true)]
    public void A_payment_above_the_debt_asks_MSG_DEBT_04_with_the_money_owed_back_after_it(int debt, int amount, bool asked)
    {
        var question = CustomerPaymentRules.AboveDebtQuestion("Bà Lan", debt, amount, confirmed: false);

        Assert.Equal(asked, question is not null);
        Assert.Null(CustomerPaymentRules.AboveDebtQuestion("Bà Lan", debt, amount, confirmed: true));
        if (question is not null)
        {
            Assert.Equal((ErrorKind.Conflict, "MSG-DEBT-04", "confirmAboveDebt"), (question.Kind, question.Message.Code, question.Details!["confirmationField"]));
            Assert.Equal((decimal)(amount - debt), question.Details["owedBackAmount"]);
        }
    }

    [Fact]
    public void The_questions_and_the_refusal_read_as_the_srs_words_them()
    {
        Assert.Equal(
            "Bà Lan owes the cooperative 1,000,000 VND. After this payment of 1,200,000 VND, the cooperative will owe Bà Lan 200,000 VND. Do you want to save the payment?",
            CustomerPaymentRules.AboveDebtQuestion("Bà Lan", 1_000_000m, 1_200_000m, false)!.Message.Text);
        Assert.StartsWith("The cooperative already owes Bà Lan 50,000 VND. After this payment of 100,000 VND, the cooperative will owe Bà Lan 150,000 VND.",
            CustomerPaymentRules.AboveDebtQuestion("Bà Lan", -50_000m, 100_000m, false)!.Message.Text);

        var existing = new CustomerPayment { Code = "PT000007", Amount = 500_000m, PaymentDate = Today, BankReference = "FT-DEMO-1", Customer = new Customer { Name = "Bà Lan" } };
        var duplicate = CustomerPaymentRules.DuplicateBankReferenceQuestion(existing);
        Assert.Equal(("MSG-DEBT-05", "confirmDuplicateBankReference"), (duplicate.Message.Code, duplicate.Details!["confirmationField"]));
        Assert.Equal("Payment PT000007 (500,000 VND, 08/11/2026, Bà Lan) already has bank reference FT-DEMO-1. Do you want to save this one as well?", duplicate.Message.Text);
        existing.Customer = null;
        Assert.Contains("(500,000 VND, 08/11/2026) already", CustomerPaymentRules.DuplicateBankReferenceQuestion(existing).Message.Text);

        // DEBT-02.3: up to the money owed back, never more; nothing can be paid back while the customer owes.
        Assert.Null(CustomerPaymentRules.PaidBackAboveOwedBack("Bà Lan", -300_000m, 300_000m));
        var refused = CustomerPaymentRules.PaidBackAboveOwedBack("Bà Lan", -300_000m, 300_001m)!;
        Assert.Equal((ErrorKind.Validation, "MSG-DEBT-06", "Amount", 300_000m), (refused.Kind, refused.Message.Code, refused.Details!["field"], refused.Details["owedBackAmount"]));
        Assert.Equal("The cooperative owes Bà Lan 300,000 VND. The money paid back cannot be more than this amount.", refused.Message.Text);
        Assert.Equal(0m, CustomerPaymentRules.PaidBackAboveOwedBack("Bà Lan", 10_000m, 1m)!.Details!["owedBackAmount"]);
    }

    [Fact]
    public void A_payment_lowers_the_debt_money_paid_back_raises_it_and_a_cancellation_undoes_either()
    {
        Assert.Equal(600_000m, CustomerPaymentRules.DebtAfter(1_000_000m, CustomerPaymentKind.Payment, 400_000m, cancelled: false));
        Assert.Equal(1_400_000m, CustomerPaymentRules.DebtAfter(1_000_000m, CustomerPaymentKind.Payment, 400_000m, cancelled: true));
        Assert.Equal(0m, CustomerPaymentRules.DebtAfter(-200_000m, CustomerPaymentKind.PaidBack, 200_000m, cancelled: false));
        Assert.Equal(-200_000m, CustomerPaymentRules.DebtAfter(0m, CustomerPaymentKind.PaidBack, 200_000m, cancelled: true));
    }

    // MSG-DEBT-03 "{user_name} {transaction_text}. {debt_text}" (BR-DEBT-06), and its SMS: a money type, code and amount only (OMTS-216).
    [Fact]
    public void The_owner_notice_names_the_transaction_and_the_debt_after_it_and_one_list_of_confirmations_gives_one_notice()
    {
        Assert.Equal("Kế toán Hà recorded payment PT000010 of 1,000,000 VND from Bà Lan. Bà Lan now owes the cooperative 2,000,000 VND.",
            UserMessages.CustomerTransaction("Kế toán Hà", "Payment", "PT000010", 1_000_000m, "Bà Lan", 2_000_000m).Text);
        Assert.Equal("Kế toán Hà recorded money paid back PT000011 of 200,000 VND to Bà Lan. Bà Lan now owes the cooperative 0 VND.",
            UserMessages.CustomerTransaction("Kế toán Hà", "PaidBack", "PT000011", 200_000m, "Bà Lan", 0m).Text);
        Assert.Equal("Kế toán Hà cancelled payment PT000012 of 1,500,000 VND from Bà Lan. The cooperative now owes Bà Lan 100,000 VND.",
            UserMessages.CustomerTransaction("Kế toán Hà", "PaymentCancelled", "PT000012", 1_500_000m, "Bà Lan", -100_000m).Text);
        Assert.Equal("Kế toán Hà cancelled payment PT000013 of 300,000 VND with no customer profile.",
            UserMessages.CustomerTransaction("Kế toán Hà", "PaymentCancelled", "PT000013", 300_000m, null, null).Text);

        var batch = UserMessages.CustomerPaymentsBatch("Kế toán Hà", [("PT000001", 4_000_000m), ("PT000002", 1_000_000m)], [("PT000003", 300_000m)], [("Bà Lan", 800_000m)]);
        Assert.Equal(
            "Kế toán Hà confirmed 2 payments (PT000001, PT000002) of 5,000,000 VND and cancelled 1 payment (PT000003) of 300,000 VND. Bà Lan now owes the cooperative 800,000 VND.",
            batch.Text);
        Assert.Equal(("MSG-DEBT-03", 5_300_000m), (batch.Code, batch.Parameters!["amount"]));
        Assert.Equal("Kế toán Hà confirmed 1 payment (PT000001) of 4,000,000 VND. The customer debts have not changed.",
            UserMessages.CustomerPaymentsBatch("Kế toán Hà", [("PT000001", 4_000_000m)], [], []).Text);

        var type = NotificationTypes.Find(NotificationTypes.CustomerPaymentTransaction)!;
        Assert.True(type.IsMandatory);
        var single = new NotificationContent(type.Code, "t", UserMessages.CustomerTransaction("Kế toán Hà", "Payment", "PT000010", 1_000_000m, "Bà Lan", 2_000_000m),
            new RelatedRecord(RelatedRecordTypes.CustomerPayment, Guid.NewGuid(), "PT000010"));
        Assert.Equal("Customer payment transaction PT000010: 1,000,000 VND.", NotificationSmsText.Render(type, type.Sms!, single));
        // The SMS of a list shows the total confirmed and the total cancelled apart (PM answer 12/11/2026, OMTS-224 [1.4]), a part of 0 left out; the "amount" of the push and the list stays their sum.
        Assert.Equal("Customer payment transaction: thu 5,000,000 VND, huy 300,000 VND.", NotificationSmsText.Render(type, type.Sms!, new NotificationContent(type.Code, "t", batch)));
        var onlyConfirmed = UserMessages.CustomerPaymentsBatch("Kế toán Hà", [("PT000001", 4_000_000m)], [], []);
        var onlyCancelled = UserMessages.CustomerPaymentsBatch("Kế toán Hà", [], [("PT000003", 300_000m)], [("Bà Lan", 800_000m)]);
        Assert.Equal("Customer payment transaction PT000001: thu 4,000,000 VND.", NotificationSmsText.Render(type, type.Sms!, new NotificationContent(type.Code, "t", onlyConfirmed, new RelatedRecord(RelatedRecordTypes.CustomerPayment, Guid.NewGuid(), "PT000001"))));
        Assert.Equal("Customer payment transaction: huy 300,000 VND.", NotificationSmsText.Render(type, type.Sms!, new NotificationContent(type.Code, "t", onlyCancelled)));
        Assert.Equal(5_300_000m, batch.Parameters!["amount"]);
    }

    // A long list names the first 10 codes of each part and the first 5 debts, and counts the rest, in the text and in the parameters (the row holds 2000 characters).
    [Fact]
    public void A_long_list_names_the_first_codes_and_debts_and_counts_the_rest()
    {
        var confirmed = Enumerable.Range(1, 12).Select(i => ($"PT{i:D6}", 1_000m)).ToList();
        var debts = Enumerable.Range(1, 7).Select(i => ($"Khách {i}", i * 1_000m)).ToList();

        var batch = UserMessages.CustomerPaymentsBatch("Kế toán Hà", confirmed, [("PT000100", 500m)], debts);

        Assert.StartsWith(
            "Kế toán Hà confirmed 12 payments (PT000001, PT000002, PT000003, PT000004, PT000005, PT000006, PT000007, PT000008, PT000009, PT000010 and 2 more) of 12,000 VND"
            + " and cancelled 1 payment (PT000100) of 500 VND. Khách 1 now owes the cooperative 1,000 VND.",
            batch.Text);
        Assert.EndsWith("Khách 5 now owes the cooperative 5,000 VND. The debts of 2 more customers have changed.", batch.Text);
        var parameters = batch.Parameters!;
        Assert.Equal(10, ((IReadOnlyCollection<string>)parameters["confirmed_codes"]!).Count);
        Assert.Equal((12, 2, 1, 0, 2), ((int)parameters["confirmed_count"]!, (int)parameters["confirmed_more_count"]!, (int)parameters["cancelled_count"]!,
            (int)parameters["cancelled_more_count"]!, (int)parameters["debts_more_count"]!));
        Assert.Equal(5, ((System.Collections.ICollection)parameters["debts"]!).Count);
    }

    // BR-DEBT-04: a cancelled payment stays on its own date, counts 0 and carries its cancel date.
    [Fact]
    public void A_cancelled_payment_stays_in_the_statement_with_its_cancel_date()
    {
        var customer = Guid.NewGuid();
        var cancelledAt = new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero);
        var invoice = new CustomerLedgerInvoice(customer, Guid.NewGuid(), "HD000001", cancelledAt.AddDays(-5), Today.AddDays(-5), 1_000_000m, false);
        var payment = new CustomerLedgerPayment(
            customer, Guid.NewGuid(), "PT000001", CustomerPaymentKind.Payment, Today.AddDays(-4), 400_000m, CustomerPaymentMethod.Cash, null, null,
            StatusIds.CustomerPayment.Cancelled, cancelledAt);

        var lines = CustomerStatementLedger.Build([invoice], [payment], Today.AddDays(-10), Today);

        var entry = lines.Entries[1];
        Assert.Equal((true, 0m, 1_000_000m, cancelledAt), (entry.Cancelled, entry.DebtChange, entry.DebtAfter, entry.CancelledAt));
        Assert.Null(lines.Entries[0].CancelledAt);
    }
}
