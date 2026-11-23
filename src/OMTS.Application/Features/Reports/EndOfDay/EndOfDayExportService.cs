using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Reports.Documents;

namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>
/// The report as a printable file in the KiotViet style: <c>BaoCaoCuoiNgay_{BanHang|ThuChi|HangHoa|TongHop}_{yyyyMMdd}[-{yyyyMMdd}].xlsx</c> (default) or <c>.pdf</c>, chosen with
/// <c>format</c>, on A4 portrait (default) or landscape, chosen with <c>orientation</c>. <c>expanded</c> adds the documents under each product of the goods. The same selection as the
/// screen, with the Export right. Changes nothing.
/// </summary>
public sealed class EndOfDayExportService(EndOfDayService report, ReportDocumentExport documents, ICooperativeRepository cooperatives, TimeProvider timeProvider)
{
    public const string OrientationField = "orientation";

    /// <summary>Portrait when none is given; "portrait" or "landscape" in any letter case; anything else is not ok.</summary>
    public static (ReportOrientation Orientation, bool Ok) ParseOrientation(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "portrait" => (ReportOrientation.Portrait, true),
        "landscape" => (ReportOrientation.Landscape, true),
        _ => (ReportOrientation.Portrait, false),
    };

    public async Task<Result<ExportedFile>> ExportAsync(EndOfDayQuery request, string? format, string? orientation, bool expanded, CancellationToken cancellationToken)
    {
        var (fileFormat, formatOk) = ReportDocumentExport.ParseFormat(format);
        var (paper, orientationOk) = ParseOrientation(orientation);
        if (!formatOk || !orientationOk)
        {
            var wrong = new List<string>();
            if (!formatOk)
            {
                wrong.Add(ReportDocumentExport.FormatField);
            }

            if (!orientationOk)
            {
                wrong.Add(OrientationField);
            }

            return wrong.Count == 1 ? AppError.Invalid(wrong[0]) : AppError.InvalidFields(wrong);
        }

        var found = await report.BuildAsync(request, PermissionAction.Export, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var shown = found.Value!;
        var concern = Enum.Parse<EndOfDayConcern>(shown.Report.Concern, ignoreCase: true);
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        var document = EndOfDayDocumentBuilder.Build(
            shown.Report, shown.Period, cooperative?.ShortName ?? cooperative?.FullName ?? string.Empty, timeProvider.GetUtcNow(), paper, expanded);
        return documents.Write(fileFormat, EndOfDayDocumentBuilder.FileBaseName(concern, shown.Period), document);
    }
}
