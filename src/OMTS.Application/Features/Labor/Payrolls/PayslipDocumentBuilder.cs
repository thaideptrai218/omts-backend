using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Features.Reports.Documents;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// One payslip as a printable "Phiếu lương": the employee and period, the income by job type (hours x rate or shifts x rate), the bonus and deductions, the net pay with what is paid and owed, the payments, and the day by day
/// attendance that explains the hours and shifts. No identity number and no bank account are printed. Changes nothing.
/// </summary>
public static class PayslipDocumentBuilder
{
    public static ReportDocument Build(PayrollSlipDetail slip, string cooperativeName, DateTimeOffset createdAt)
    {
        var sections = new List<ReportSection> { Income(slip) };
        var payments = slip.Payments.Where(p => p.Status == "Recorded").ToList();
        if (payments.Count > 0)
        {
            sections.Add(Payments(payments));
        }

        if (slip.Attendance.Count > 0)
        {
            sections.Add(Attendance(slip));
        }

        var filters = new List<string>
        {
            $"Nhân viên: {slip.EmployeeName}{(string.IsNullOrEmpty(slip.EmployeeCode) ? string.Empty : $" ({slip.EmployeeCode})")}",
            $"Kỳ làm việc: {PayrollSheetDocumentBuilder.Day(slip.From)} - {PayrollSheetDocumentBuilder.Day(slip.To)}",
            $"Bảng lương: {slip.PayrollCode}",
        };
        return new ReportDocument($"Phiếu lương {slip.Code}", createdAt.ToOffset(BusinessClock.UtcOffset).DateTime, filters, sections, cooperativeName, ReportOrientation.Landscape);
    }

    private static ReportSection Income(PayrollSlipDetail slip)
    {
        ReportColumn[] columns =
        [
            new("Khoản mục", 34), new("Số giờ", 12, ReportAlign.Right), new("Số ca", 10, ReportAlign.Right), new("Đơn giá", 14, ReportAlign.Right, ReportCellFormat.Integer),
            new("Thành tiền", 16, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        var rows = new List<ReportRow> { ReportRow.Group("Thu nhập", null, null, null, slip.Income) };
        if (slip.FixedPay > 0)
        {
            rows.Add(ReportRow.Detail("Lương cố định", null, null, null, slip.FixedPay));
        }

        rows.AddRange(slip.Lines.Select(l => l.Kind switch
        {
            PayLineKind.Hourly => ReportRow.Detail(l.JobName, PayrollRules.HoursText(l.Hours), null, l.HourlyRate, l.Amount),
            PayLineKind.FieldShift => ReportRow.Detail($"{l.JobName} (tiền ca)", PayrollRules.HoursText(l.Hours), l.Shifts, l.ShiftRate, l.Amount),
            _ => ReportRow.Detail($"{l.JobName} (theo ca)", PayrollRules.HoursText(l.Hours), l.Shifts, l.ShiftRate, l.Amount),
        }));
        rows.Add(ReportRow.Detail("Thưởng", null, null, null, slip.Bonus));
        rows.Add(ReportRow.Detail("Khấu trừ", null, null, null, slip.ManualDeduction));
        rows.AddRange(slip.Adjustments?.Select(a => ReportRow.Detail($"  {(a.Kind == PayrollAdjustmentKind.Bonus ? "+" : "−")} {a.Reason}", null, null, null, a.Amount)) ?? []);
        rows.Add(ReportRow.Group("Giảm trừ (tạm ứng đã trừ)", null, null, null, slip.AdvanceDeduction));
        rows.Add(ReportRow.Group("Thực lĩnh", PayrollRules.HoursText(slip.Hours), slip.Shifts, null, slip.Net));
        rows.Add(ReportRow.Detail("Đã trả", null, null, null, slip.Paid));
        rows.Add(ReportRow.Group("Còn cần trả", null, null, null, slip.Outstanding));
        return new ReportSection("Thu nhập", columns, rows);
    }

    private static ReportSection Payments(IReadOnlyList<PayrollSlipPayment> payments)
    {
        ReportColumn[] columns =
        [
            new("Ngày trả", 16), new("Mã phiếu chi", 16, IsCode: true), new("Hình thức", 16), new("Loại", 16), new("Số tiền", 16, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        var rows = payments.Select(p => ReportRow.Detail(
            p.PaidAt.ToOffset(BusinessClock.UtcOffset).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), p.VoucherCode,
            p.Method == PaymentMethod.Cash ? "Tiền mặt" : "Chuyển khoản", p.Kind == WagePaymentKind.Advance ? "Tạm ứng" : "Trả lương", p.Amount)).ToList();
        return new ReportSection("Thanh toán", columns, rows);
    }

    private static ReportSection Attendance(PayrollSlipDetail slip)
    {
        ReportColumn[] columns =
        [
            new("Ngày", 12), new("Bè", 14), new("Loại việc", 20), new("Vào", 8, ReportAlign.Center), new("Ra", 8, ReportAlign.Center),
            new("Số giờ", 10, ReportAlign.Right), new("Số ca", 8, ReportAlign.Right), new("Đơn giá", 12, ReportAlign.Right, ReportCellFormat.Integer),
            new("Thành tiền", 14, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        var rows = new List<ReportRow> { ReportRow.Group("Tổng cộng", null, null, null, null, PayrollRules.HoursText(slip.Attendance.Sum(a => a.Hours)), slip.Attendance.Sum(a => a.Shifts), null, slip.Attendance.Sum(a => a.Amount)) };
        rows.AddRange(slip.Attendance.Select(a => ReportRow.Detail(
            PayrollSheetDocumentBuilder.Day(a.Date), a.RaftCodes, a.JobName, Time(a.CheckInAt), Time(a.CheckOutAt), PayrollRules.HoursText(a.Hours),
            a.Kind == PayLineKind.Hourly ? null : a.Shifts, a.Kind == PayLineKind.Hourly ? a.HourlyRate : a.ShiftRate, a.Amount)));
        return new ReportSection("Chấm công chi tiết", columns, rows);
    }

    private static string? Time(DateTimeOffset? instant) => instant?.ToOffset(BusinessClock.UtcOffset).ToString("HH:mm", CultureInfo.InvariantCulture);
}