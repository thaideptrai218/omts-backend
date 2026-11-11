using System.Globalization;

namespace OMTS.Application.Common.Messages;

/// <summary>
/// The MSG-LABOR messages of payroll sheets and wage payments (LABOR-12, LABOR-13, LABOR-14): the wording is the SRS v8 one with its
/// {placeholders} filled in and listed in <see cref="UserMessage.Parameters"/>. Where this payroll differs from SRS v8 (no sending to the
/// Owner, who does everything) the text says "finalized" for "sent".
/// </summary>
public static class PayrollMessages
{
    /// <summary>MSG-LABOR-28 (LABOR-12.0.E3, BR-LABOR-10): the payroll cannot be finalized yet; <paramref name="reasons"/> lists what is missing.</summary>
    public static UserMessage NotReady(IReadOnlyList<string> reasons) => new(
        MessageCodes.Labor28, $"The payroll cannot be finalized yet: {string.Join("; ", reasons)}.")
    {
        Parameters = new Dictionary<string, object?> { ["reason_list"] = reasons },
    };

    /// <summary>MSG-LABOR-31 (LABOR-13.0.E1): requests for days of the period are awaiting review.</summary>
    public static UserMessage RequestsAwaitingReview(string period, int count, string requestList) => new(
        MessageCodes.Labor31,
        $"The payroll of {period} cannot be finalized yet: {count} employee requests for days of the period are awaiting review ({requestList}).")
    {
        Parameters = new Dictionary<string, object?> { ["period"] = period, ["count"] = count, ["request_list"] = requestList },
    };

    /// <summary>MSG-LABOR-34 (LABOR-14.0.E2, BR-LABOR-11): the amount paid to an employee is more than the amount still owed.</summary>
    public static UserMessage MoreThanOwed(string employeeName, decimal owed) => new(
        MessageCodes.Labor34, $"{employeeName}: the amount cannot be more than the {Vnd(owed)} VND still owed.")
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName, ["owed_amount"] = owed },
    };

    private static string Vnd(decimal amount) => amount.ToString("#,##0", CultureInfo.InvariantCulture);
}
