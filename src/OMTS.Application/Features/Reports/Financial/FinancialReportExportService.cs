using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Reports.Financial;

/// <summary>
/// The statement shown as an .xlsx file <c>financial-{unit}-{from}-{to}.xlsx</c>: the sheet "Financial" (one row per line, one column per period and a Total column, the codes of
/// <see cref="FinancialRow"/> as the line names) and the sheet "Choices". The same selection as the screen, with the Export right. Changes nothing.
/// </summary>
public sealed class FinancialReportExportService(FinancialReportService report, ISpreadsheetWriter spreadsheets)
{
    public const string FiguresSheetName = "Financial";

    public async Task<Result<ExportedFile>> ExportAsync(FinancialReportQuery request, CancellationToken cancellationToken)
    {
        var found = await report.BuildAsync(request, PermissionAction.Export, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var shown = found.Value!;
        var headers = new List<string> { "Line" };
        headers.AddRange(shown.Columns.Select(c => c.Label));
        headers.Add("Total");
        var rows = shown.Rows
            .Select(r => (IReadOnlyList<object?>)new List<object?> { r.Code }.Concat(r.Values.Cast<object?>()).Append(r.Total).ToList())
            .ToList();
        var choices = ReportExportFile.ChoicesSheet(shown.From, shown.To, [], [], [], [("Unit", shown.Unit)]);
        var fileName = $"financial-{shown.Unit}-{shown.From:yyyyMMdd}-{shown.To:yyyyMMdd}.xlsx";
        return ReportExportFile.Write(spreadsheets, fileName, [new SpreadsheetSheet(FiguresSheetName, headers, rows), choices]);
    }
}
