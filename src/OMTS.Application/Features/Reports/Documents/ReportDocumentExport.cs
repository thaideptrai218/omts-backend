using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;

namespace OMTS.Application.Features.Reports.Documents;

/// <summary>What every document-style report export shares: reading the <c>format</c> query value and writing the file with the renderer of that format.</summary>
public sealed class ReportDocumentExport(IEnumerable<IReportDocumentRenderer> renderers)
{
    public const string FormatField = "format";

    /// <summary>Xlsx when none is given (the exports that existed before the PDF); "xlsx" or "pdf" in any letter case; anything else is not ok.</summary>
    public static (ReportFileFormat Format, bool Ok) ParseFormat(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "xlsx" => (ReportFileFormat.Xlsx, true),
        "pdf" => (ReportFileFormat.Pdf, true),
        _ => (ReportFileFormat.Xlsx, false),
    };

    /// <summary>The file named <c>{baseName}.{extension}</c>; a renderer that fails gives the 500 file message of the other exports, never a half file.</summary>
    public Result<ExportedFile> Write(ReportFileFormat format, string baseName, ReportDocument document)
    {
        var renderer = renderers.FirstOrDefault(r => r.Format == format);
        if (renderer is null)
        {
            return AppError.Invalid(FormatField);
        }

        try
        {
            return new ExportedFile($"{baseName}.{renderer.Extension}", renderer.ContentType, renderer.Render(document));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }
}
