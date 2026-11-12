using System.Globalization;

namespace OMTS.Application.Common.Messages;

/// <summary>
/// The MSG-LABOR messages of work assignment, attendance and employee requests (LABOR-02, LABOR-04, LABOR-05, LABOR-10); the wording is
/// the SRS v8 one, with its {placeholders} filled in and listed in <see cref="UserMessage.Parameters"/>. The rest of FE-09 is in
/// <see cref="UserMessages"/>.
/// </summary>
public static class WorkMessages
{
    /// <summary>MSG-LABOR-06: a worker is already assigned to another raft that day and is not checked in yet; the client asks, then sends <c>moveExisting</c>.</summary>
    public static UserMessage MoveToRaft(string employeeName, string raftCode, DateOnly date, string newRaftCode) => new(
        MessageCodes.Labor06, $"{employeeName} is already assigned to raft {raftCode} on {Date(date)}. Move {employeeName} to raft {newRaftCode}?")
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName, ["raft_code"] = raftCode, ["date"] = date, ["new_raft_code"] = newRaftCode },
    };

    /// <summary>MSG-LABOR-07: a selected employee is already checked in on that date; a checked-in employee is moved with Change job for crew.</summary>
    public static UserMessage AlreadyCheckedIn(string employeeName) => new(
        MessageCodes.Labor07, $"{employeeName} is already checked in today. To move {employeeName} to another raft, use Change job for crew.")
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName },
    };

    /// <summary>MSG-LABOR-12: a check-out is recorded for an employee who has no check-in that day (LABOR-04.6).</summary>
    public static UserMessage CheckOutWithoutCheckIn(string employeeName, DateTimeOffset time) => new(
        MessageCodes.Labor12,
        $"{employeeName} has no check-in today. The check-out at {Time(time)} is saved, but the day is not counted until the Cooperative Owner corrects the check-in or approves a request from the employee.")
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName, ["time"] = time },
    };

    /// <summary>MSG-LABOR-13: the scanned QR code is not an attendance QR code of OMTS, has expired or is not the employee's.</summary>
    public static readonly UserMessage QrCodeNotRecognized = new(
        MessageCodes.Labor13, "This QR code is not recognized or has expired. Ask the employee to show the current code, or mark the attendance manually.");

    /// <summary>MSG-LABOR-14: the employee already has a check-in, or a check-out, that day.</summary>
    /// <param name="checkInOrCheckOut">"check-in" or "check-out".</param>
    public static UserMessage AlreadyRecorded(string employeeName, string checkInOrCheckOut, DateTimeOffset time) => new(
        MessageCodes.Labor14, $"{employeeName} already has a {checkInOrCheckOut} today, at {Time(time)}.")
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName, ["check_in_or_check_out"] = checkInOrCheckOut, ["time"] = time },
    };

    /// <summary>MSG-LABOR-16: the approval of a request cannot be applied (LABOR-05.0.E1); the Owner can reject it with a note.</summary>
    public static UserMessage RequestCannotBeApproved(string cause) => new(
        MessageCodes.Labor16, $"This request cannot be approved: {cause}. You can reject it with a note.")
    {
        Parameters = new Dictionary<string, object?> { ["cause"] = cause },
    };

    /// <summary>MSG-LABOR-22: an employee request is sent to the Cooperative Owner (LABOR-10 step 5).</summary>
    public static UserMessage RequestSent(string requestText) => new(
        MessageCodes.Labor22, $"{requestText} has been sent to the Cooperative Owner.")
    {
        Parameters = new Dictionary<string, object?> { ["object_name"] = requestText },
    };

    /// <summary>MSG-LABOR-23: notification to the Cooperative Owner when an employee sends a request.</summary>
    public static UserMessage RequestNotice(string employeeName, string requestText, string reason) => new(
        MessageCodes.Labor23, $"{employeeName} sent a request {requestText}. Reason: {reason}.")
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName, ["request_text"] = requestText, ["reason"] = reason },
    };

    /// <summary>MSG-LABOR-25: a day of a request is in a pay period whose payroll is Finalized (LABOR-10.0.E2).</summary>
    public static UserMessage PayFinalized(DateOnly date) => new(
        MessageCodes.Labor25, $"The pay for {Date(date)} is already finalized. You cannot send a request for this day.")
    {
        Parameters = new Dictionary<string, object?> { ["date"] = date },
    };

    /// <summary>MSG-LABOR-26: the request was approved or rejected before the employee could cancel it (LABOR-10.2.E1).</summary>
    /// <param name="result">"approved" or "rejected".</param>
    public static UserMessage AlreadyReviewed(string result) => new(
        MessageCodes.Labor26, $"This request was already {result} by the Cooperative Owner, so it can no longer be cancelled.")
    {
        Parameters = new Dictionary<string, object?> { ["result"] = result },
    };

    /// <summary>How a request is named in messages, e.g. "for leave from 01/10/2026 to 03/10/2026".</summary>
    public static string RequestText(Domain.Enums.EmployeeRequestType type, DateOnly from, DateOnly to, TimeOnly? time) => type switch
    {
        Domain.Enums.EmployeeRequestType.Leave when from == to => $"for leave on {Date(from)}",
        Domain.Enums.EmployeeRequestType.Leave => $"for leave from {Date(from)} to {Date(to)}",
        Domain.Enums.EmployeeRequestType.MissedCheckIn => $"for a missed check-in on {Date(from)} at {time:HH:mm}",
        _ => $"for a missed check-out on {Date(from)} at {time:HH:mm}",
    };

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset time) => time.ToOffset(BusinessClock.UtcOffset).ToString("HH:mm", CultureInfo.InvariantCulture);
}
