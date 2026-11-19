using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Reports.Documents;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// The payroll sheets as .xlsx files (LABOR-12): one sheet with its payslips, or the list as filtered and sorted. The Owner only. File names
/// hold letters, digits, dot, dash and underscore only. Text cells are written as text, so a name such as =1+1 stays text.
/// </summary>
public sealed class PayrollExportService(
    PayrollQueryService queries, ISpreadsheetWriter spreadsheets, ReportDocumentExport documents, ICooperativeRepository cooperatives, TimeProvider timeProvider)
{
    /// <summary>The most sheets one list file holds.</summary>
    public const int MaxListRows = 5000;

    private static readonly string[] SlipHeaders =
    [
        "Mã phiếu lương", "Mã nhân viên", "Tên nhân viên", "Số giờ", "Lương chính", "Thưởng", "Giảm trừ", "Tổng thu nhập", "Lương thực nhận", "Đã trả", "Còn cần trả",
    ];

    private static readonly string[] SheetHeaders =
    [
        "Mã bảng lương", "Tên bảng lương", "Kỳ lương", "Từ ngày", "Đến ngày", "Tổng lương", "Đã trả", "Còn cần trả", "Trạng thái", "Ngày tạo",
    ];

    public async Task<Result<ExportedFile>> ExportSheetAsync(Guid id, string? format, CancellationToken cancellationToken)
    {
        var (fileFormat, formatOk) = ReportDocumentExport.ParseFormat(format);
        if (!formatOk)
        {
            return AppError.Invalid(ReportDocumentExport.FormatField);
        }

        var detail = await queries.GetAsync(id, cancellationToken);
        if (!detail.IsSuccess)
        {
            return detail.Error!;
        }

        var sheet = detail.Value!;
        if (fileFormat == ReportFileFormat.Pdf)
        {
            var document = PayrollSheetDocumentBuilder.Build(sheet, await CooperativeNameAsync(cancellationToken), timeProvider.GetUtcNow());
            return documents.Write(fileFormat, $"BangLuong_{PayrollRules.SafeFileName(sheet.Code)}", document);
        }

        var rows = sheet.Slips.Select(s => (IReadOnlyList<object?>)
        [
            s.Code, s.EmployeeCode, s.EmployeeName, s.Hours, s.MainPay, s.Bonus, s.Deduction, s.Income, s.Net, s.Paid, s.Outstanding,
        ]).ToList();
        rows.Add(["Tổng", null, null, sheet.Slips.Sum(s => s.Hours), sheet.Slips.Sum(s => s.MainPay), sheet.Slips.Sum(s => s.Bonus), sheet.Slips.Sum(s => s.Deduction),
            sheet.Slips.Sum(s => s.Income), sheet.Total, sheet.Paid, sheet.Outstanding]);
        return Write(new SpreadsheetSheet(sheet.Code, SlipHeaders, rows), $"payroll-{PayrollRules.SafeFileName(sheet.Code)}.xlsx");
    }

    /// <summary>One payslip as a printable "Phiếu lương": <c>PhieuLuong_{code}.pdf</c> or .xlsx (default).</summary>
    public async Task<Result<ExportedFile>> ExportSlipAsync(Guid slipId, string? format, CancellationToken cancellationToken)
    {
        var (fileFormat, formatOk) = ReportDocumentExport.ParseFormat(format);
        if (!formatOk)
        {
            return AppError.Invalid(ReportDocumentExport.FormatField);
        }

        var found = await queries.GetSlipAsync(slipId, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var document = PayslipDocumentBuilder.Build(found.Value!, await CooperativeNameAsync(cancellationToken), timeProvider.GetUtcNow());
        return documents.Write(fileFormat, $"PhieuLuong_{PayrollRules.SafeFileName(found.Value!.Code)}", document);
    }

    /// <summary>The name printed in the footer of every document: the short name of the cooperative profile, else the full name.</summary>
    private async Task<string> CooperativeNameAsync(CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        return cooperative?.ShortName ?? cooperative?.FullName ?? string.Empty;
    }

    public async Task<Result<ExportedFile>> ExportListAsync(PayrollExportQuery query, CancellationToken cancellationToken)
    {
        var found = await queries.FilteredAsync(query.Search, query.PeriodType, query.Status, query.From, query.To, query.SortBy, query.SortDir, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        if (found.Value!.Count > MaxListRows)
        {
            return AppError.Invalid("Filter", $"At most {MaxListRows} payrolls: narrow the filter.");
        }

        var rows = found.Value.Select(r => (IReadOnlyList<object?>)
        [
            r.Code, r.Name, r.PeriodType == PayrollPeriodType.Monthly ? "Hàng tháng" : "Tùy chọn", Day(r.From), Day(r.To), r.Total, r.Paid, r.Outstanding,
            StatusText(r.Status), r.CreatedAt.ToOffset(BusinessClock.UtcOffset).DateTime,
        ]).ToList();
        return Write(new SpreadsheetSheet("Bảng lương", SheetHeaders, rows), $"payrolls-{timeProvider.Today():yyyyMMdd}.xlsx");
    }

    private static DateTime Day(DateOnly day) => day.ToDateTime(TimeOnly.MinValue);

    private static string StatusText(PayrollSheetStatus status) => status switch
    {
        PayrollSheetStatus.Creating => "Đang tạo",
        PayrollSheetStatus.Provisional => "Tạm tính",
        PayrollSheetStatus.Finalized => "Đã chốt lương",
        _ => "Đã hủy",
    };

    private Result<ExportedFile> Write(SpreadsheetSheet sheet, string fileName)
    {
        try
        {
            return new ExportedFile(fileName, ISpreadsheetWriter.ContentType, spreadsheets.Write([sheet]));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }
}
