using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Reports;

/// <summary>What the export of every sales report (REPORT-01 to REPORT-03) shares: the sheet that names the period and the choices, and the file written without losing the user's data on a failure.</summary>
public static class ReportExportFile
{
    public const string ChoicesSheetName = "Choices";
    public const string NoneChosen = "All";

    /// <summary>The period and what the user chose, one row each (REPORT-01.4 step 2). A choice left empty reads "All".</summary>
    public static SpreadsheetSheet ChoicesSheet(
        DateOnly from, DateOnly to, IReadOnlyList<ReportChoice> zones, IReadOnlyList<ReportChoice> customers, IReadOnlyList<ReportChoice> products,
        IEnumerable<(string Name, string Value)> reportChoices)
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { "Period from", from }, new object?[] { "Period to", to } };
        rows.AddRange(reportChoices.Select(c => (IReadOnlyList<object?>)[c.Name, c.Value]));
        rows.Add(["Zones", Names(zones)]);
        rows.Add(["Customers", Names(customers)]);
        rows.Add(["Products", Names(products)]);
        return new SpreadsheetSheet(ChoicesSheetName, ["Choice", "Value"], rows);
    }

    /// <summary>The workbook as a file; a writer that fails gives the 500 file message of the other exports, never a half file.</summary>
    public static Result<ExportedFile> Write(ISpreadsheetWriter spreadsheets, string fileName, IReadOnlyList<SpreadsheetSheet> sheets)
    {
        try
        {
            return new ExportedFile(fileName, ISpreadsheetWriter.ContentType, spreadsheets.Write(sheets));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private static string Names(IReadOnlyList<ReportChoice> choices) => choices.Count == 0 ? NoneChosen : string.Join(", ", choices.Select(c => c.Name));
}
