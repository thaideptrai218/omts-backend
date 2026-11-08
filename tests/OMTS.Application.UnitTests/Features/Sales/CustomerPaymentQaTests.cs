using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>QA of PR 8b (DEBT-02): the BR-DEBT-03 checks at their bounds, and the Owner's MSG-DEBT-03 for the largest list of confirmations the API accepts.</summary>
public class CustomerPaymentQaTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);

    // The Notification row keeps Body and MessageParams in nvarchar(2000) (Lengths.LongText); NotificationDispatcher cuts a longer body and drops longer parameters.
    private const int NotificationColumnLength = 2000;

    private static RecordCustomerPaymentRequest Cash(decimal? amount, DateOnly? date = null) =>
        new(Guid.NewGuid(), date ?? Today, amount, CustomerPaymentMethod.Cash, CollectedByAccountId: Guid.NewGuid());

    // BR-DEBT-03 "the amount is greater than 0" in whole VND (decimal(18,0)); "the payment date is not later than today".
    [Theory]
    [InlineData("1", true)]
    [InlineData("1000.0", true)]
    [InlineData("999999999999999999", true)]
    [InlineData("1000000000000000000", false)]
    [InlineData("0", false)]
    [InlineData("-1000", false)]
    [InlineData("0.4", false)]
    [InlineData("1000.5", false)]
    public void An_amount_is_a_whole_number_of_VND_above_zero_that_the_column_holds(string amount, bool valid)
    {
        var invalid = CustomerPaymentRules.InvalidFields(Cash(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)), Today);

        Assert.Equal(valid ? Array.Empty<string>() : new[] { "Amount" }, invalid);
    }

    [Fact]
    public void A_payment_dated_today_is_taken_and_tomorrow_is_refused()
    {
        Assert.Empty(CustomerPaymentRules.InvalidFields(Cash(1_000m, Today), Today));
        Assert.Empty(CustomerPaymentRules.InvalidFields(Cash(1_000m, Today.AddYears(-3)), Today));
        Assert.Equal(["Payment date"], CustomerPaymentRules.InvalidFields(Cash(1_000m, Today.AddDays(1)), Today));
    }

    // DEBT-02.1 at its edges: a payment equal to the debt is no question; one VND above it is, with 1 VND owed back. DEBT-02.3.E1: exactly the money owed back is allowed.
    [Fact]
    public void The_questions_and_the_refusal_start_one_VND_past_their_limit()
    {
        Assert.Null(CustomerPaymentRules.AboveDebtQuestion("Bà Lan", 300_000m, 300_000m, confirmed: false));
        var above = CustomerPaymentRules.AboveDebtQuestion("Bà Lan", 300_000m, 300_001m, confirmed: false)!;
        Assert.Equal((ErrorKind.Conflict, "MSG-DEBT-04", 1m), (above.Kind, above.Message.Code, above.Details!["owedBackAmount"]));

        Assert.Null(CustomerPaymentRules.PaidBackAboveOwedBack("Bà Lan", -200_000m, 200_000m));
        Assert.Equal("MSG-DEBT-06", CustomerPaymentRules.PaidBackAboveOwedBack("Bà Lan", -200_000m, 200_001m)!.Message.Code);
        Assert.Equal("MSG-DEBT-06", CustomerPaymentRules.PaidBackAboveOwedBack("Bà Lan", 0m, 1m)!.Message.Code);

        // Cancelling money paid back lowers the debt again (BR-DEBT-02: a cancelled entry counts 0).
        Assert.Equal(-200_000m, CustomerPaymentRules.DebtAfter(0m, CustomerPaymentKind.PaidBack, 200_000m, cancelled: true));
    }

    // BR-DEBT-06: the payments confirmed and cancelled from one list give one notification, MSG-DEBT-03 "{user_name} {transaction_text}. {debt_text}". The API accepts
    // lists of up to CustomerPaymentRules.MaxBatchSize payments; for such a list the notice must still fit its row whole: the in-app text keeps its debt text and the
    // clients still get the atomic parameters they word the notice from.
    [Fact]
    public void One_list_of_the_largest_size_gives_a_notice_whose_text_and_parameters_fit_the_notification_row()
    {
        var confirmed = Enumerable.Range(1, CustomerPaymentRules.MaxBatchSize - 1).Select(i => ($"PT{i:D6}", 100_000m)).ToList();
        var cancelled = new List<(string Code, decimal Amount)> { ("PT900000", 50_000m) };

        var notice = UserMessages.CustomerPaymentsBatch("Kế toán Hà", confirmed, cancelled, [("Bà Lan", 800_000m)]);

        var parameters = MessageParameterJson.Serialize(notice.Parameters)!;
        Assert.True(notice.Text.Length <= NotificationColumnLength, $"text of {notice.Text.Length} characters: the in-app notice loses its end, \"{notice.Text[^60..]}\"");
        Assert.True(parameters.Length <= NotificationColumnLength, $"parameters of {parameters.Length} characters: the row and the push carry none");
    }
}
