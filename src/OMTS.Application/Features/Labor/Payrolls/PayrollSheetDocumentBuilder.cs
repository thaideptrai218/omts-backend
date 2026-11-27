using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Features.Reports.Documents;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// A payroll sheet as a printable <see cref="ReportDocument"/> in the KiotViet layout (PDF and Excel share it): one row per payslip with the salary type, hours, shifts, main pay, shift pay, bonus,
/// manual deduction, income, advances taken off, net pay, paid and still owed, and a total row first. The footer holds the name of the cooperative. Changes nothing.
/// </summary>
public static class PayrollSheetDocumentBuilder
{
    public static ReportDocument Build(PayrollSheetDetail sheet, string cooperativeName, DateTimeOffset createdAt)
    {
        ReportColumn[] columns =
        [
            new("Mã phiếu lương", 14, IsCode: true),
            new("Mã nhân viên", 13),
            new("Tên nhân viên", 26),
            new("Loại lương", 14),
            new("Giờ công", 10, ReportAlign.Right),
            new("Số ca", 8, ReportAlign.Right),
            new("Lương chính", 14, ReportAlign.Right, ReportCellFormat.Integer),
            new("Tiền ca", 13, ReportAlign.Right, ReportCellFormat.Integer),
            new("Thưởng", 13, ReportAlign.Right, ReportCellFormat.Integer),
            new("Khấu trừ", 13, ReportAlign.Right, ReportCellFormat.Integer),
            new("Tổng thu nhập", 14, ReportAlign.Right, ReportCellFormat.Integer),
            new("Tạm ứng trừ", 13, ReportAlign.Right, ReportCellFormat.Integer),
            new("Thực lĩnh", 14, ReportAlign.Right, ReportCellFormat.Integer),
            new("Đã trả", 13, ReportAlign.Right, ReportCellFormat.Integer),
            new("Còn cần trả", 14, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        var slips = sheet.Slips;
        var rows = new List<ReportRow>
        {
            ReportRow.Group(
                $"Tổng: {slips.Count} nhân viên", null, null, null, PayrollRules.HoursText(slips.Sum(s => s.Hours)), slips.Sum(s => s.Shifts), slips.Sum(s => s.MainPay), slips.Sum(s => s.ShiftPay),
                slips.Sum(s => s.Bonus), slips.Sum(s => s.ManualDeduction), slips.Sum(s => s.Income), slips.Sum(s => s.AdvanceDeduction), sheet.Total, sheet.Paid, sheet.Outstanding),
        };
        rows.AddRange(slips.Select(s => ReportRow.Detail(
            s.Code, s.EmployeeCode, s.EmployeeName, SalaryTypeText(s.SalaryType), PayrollRules.HoursText(s.Hours), s.Shifts, s.MainPay, s.ShiftPay, s.Bonus, s.ManualDeduction, s.Income,
            s.AdvanceDeduction, s.Net, s.Paid, s.Outstanding)));
        var filters = new List<string>
        {
            $"Kỳ làm việc: {Day(sheet.From)} - {Day(sheet.To)}",
            $"Trạng thái: {StatusText(sheet.Status)}",
        };
        if (sheet.ExpenseMonth is { Length: > 0 } month)
        {
            filters.Add($"Tháng ghi chi phí: {month}");
        }

        return new ReportDocument(
            $"Bảng lương {sheet.Code}", createdAt.ToOffset(BusinessClock.UtcOffset).DateTime, filters, [new ReportSection(null, columns, rows)], cooperativeName, ReportOrientation.Landscape);
    }

    internal static string SalaryTypeText(SalaryType? type) => type switch
    {
        SalaryType.FixedMonthly => "Cố định tháng",
        SalaryType.PerShift => "Theo ca",
        SalaryType.Hourly => "Theo giờ",
        _ => "Chưa thiết lập lương",
    };

    internal static string Day(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    internal static string StatusText(PayrollSheetStatus status) => status switch
    {
        PayrollSheetStatus.Creating => "Đang tạo",
        PayrollSheetStatus.Provisional => "Tạm tính",
        PayrollSheetStatus.Finalized => "Đã chốt lương",
        _ => "Đã hủy",
    };
}