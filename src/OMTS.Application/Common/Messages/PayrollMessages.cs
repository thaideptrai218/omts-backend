using System.Globalization;

namespace OMTS.Application.Common.Messages;

/// <summary>
/// The MSG-LABOR messages of payroll sheets and wage payments (LABOR-12, LABOR-13, LABOR-14). The text is Vietnamese (the Owner reads it as it is, with the
/// names and dates in it) and every value it is built from is also in <see cref="UserMessage.Parameters"/> and in the <c>parameters</c> member of the error
/// response, so a client can fill its own template. Where this payroll differs from SRS v8 (no sending to the Owner, who does everything) the text says
/// "chốt" (finalize) for "sent".
/// </summary>
public static class PayrollMessages
{
    /// <summary>MSG-LABOR-28 (LABOR-12.0.E3, BR-LABOR-10): the payroll cannot be finalized yet; <paramref name="reasons"/> lists what is missing, each with names and dates.</summary>
    public static UserMessage NotReady(IReadOnlyList<string> reasons) => new(
        MessageCodes.Labor28, $"Chưa thể chốt bảng lương: {string.Join("; ", reasons)}.")
    {
        Parameters = new Dictionary<string, object?> { ["reason_list"] = reasons },
    };

    /// <summary>MSG-LABOR-31 (LABOR-13.0.E1): requests for days of the period are awaiting review.</summary>
    public static UserMessage RequestsAwaitingReview(string period, int count, string requestList) => new(
        MessageCodes.Labor31,
        $"Chưa thể chốt bảng lương {period}: còn {count} yêu cầu của nhân viên trong kỳ chờ duyệt ({requestList}).")
    {
        Parameters = new Dictionary<string, object?> { ["period"] = period, ["count"] = count, ["request_list"] = requestList },
    };

    /// <summary>MSG-LABOR-34 (LABOR-14.0.E2, BR-LABOR-11): the amount paid to an employee is more than the amount still owed.</summary>
    public static UserMessage MoreThanOwed(string employeeName, decimal owed) => new(
        MessageCodes.Labor34, $"{employeeName}: số tiền không được lớn hơn {Vnd(owed)} đ còn phải trả.")
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName, ["owed_amount"] = owed },
    };

    /// <summary>
    /// The totals on the screen are not the totals of the sheet recalculated now (L11): nothing is finalized, the Owner looks again and confirms. Code MSG-LABOR-28, as the sheet is not ready to finalize
    /// in the numbers the Owner approved.
    /// </summary>
    public static UserMessage TotalsChanged(string period, int slipsBefore, int slipsNow, decimal totalBefore, decimal totalNow) => new(
        MessageCodes.Labor28,
        $"Chưa thể chốt bảng lương {period}: số liệu đã thay đổi từ lần xem trước. Tổng thu nhập {Vnd(totalBefore)} đ ({slipsBefore} phiếu) nay là {Vnd(totalNow)} đ ({slipsNow} phiếu). Hãy kiểm tra lại rồi chốt.")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["period"] = period, ["slips_before"] = slipsBefore, ["slips_now"] = slipsNow, ["total_before"] = totalBefore, ["total_now"] = totalNow,
        },
    };

    /// <summary>MSG-GEN-09 style (L16): the payment is dated before the payroll it pays was finalized.</summary>
    public static UserMessage PaidBeforeFinalized(string payrollCode, DateOnly finalizedOn) => new(
        MessageCodes.Gen09, $"Ngày chi không được trước ngày chốt bảng lương {payrollCode} ({Date(finalizedOn)}).", MessageCodes.Legacy.Msg61)
    {
        Parameters = new Dictionary<string, object?> { ["payroll_code"] = payrollCode, ["finalized_on"] = finalizedOn },
    };

    /// <summary>MSG-GEN-09 style (L7): a wage or advance voucher is made by the payroll module and is not cancelled from the cash book.</summary>
    public static UserMessage PayrollVoucherNotCancellable(string voucherCode) => new(
        MessageCodes.Gen09, $"Phiếu chi {voucherCode} là phiếu chi lương do hệ thống tạo, không thể hủy từ Sổ quỹ.", MessageCodes.Legacy.Msg61)
    {
        Parameters = new Dictionary<string, object?> { ["voucher_code"] = voucherCode },
    };

    /// <summary>MSG-GEN-09 style: an advance that a later payroll already took off cannot be cancelled.</summary>
    public static UserMessage AdvanceAlreadyDeducted(string voucherCode, decimal deducted) => new(
        MessageCodes.Gen09, $"Không thể hủy phiếu chi {voucherCode}: khoản tạm ứng đã được trừ {Vnd(deducted)} đ vào bảng lương sau đó.", MessageCodes.Legacy.Msg61)
    {
        Parameters = new Dictionary<string, object?> { ["voucher_code"] = voucherCode, ["deducted"] = deducted },
    };

    public static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    public static string ShortDate(DateOnly day) => day.ToString("dd/MM", CultureInfo.InvariantCulture);

    private static string Vnd(decimal amount) => amount.ToString("#,##0", CultureInfo.InvariantCulture).Replace(',', '.');
}
