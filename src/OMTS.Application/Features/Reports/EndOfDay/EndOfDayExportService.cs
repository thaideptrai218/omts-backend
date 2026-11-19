using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Reports.Documents;

namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>
/// The report as a printable file in the KiotViet style: <c>BaoCaoCuoiNgay_{yyyyMMdd}.xlsx</c> (default) or <c>.pdf</c>, chosen with <c>format</c>. The same selection as the screen, with the
/// Export right. Changes nothing.
/// </summary>
public sealed class EndOfDayExportService(EndOfDayService report, ReportDocumentExport documents, ICooperativeRepository cooperatives, TimeProvider timeProvider)
{
    public async Task<Result<ExportedFile>> ExportAsync(EndOfDayQuery request, string? format, CancellationToken cancellationToken)
    {
        var (fileFormat, formatOk) = ReportDocumentExport.ParseFormat(format);
        if (!formatOk)
        {
            return AppError.Invalid(ReportDocumentExport.FormatField);
        }

        var found = await report.BuildAsync(request, PermissionAction.Export, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var shown = found.Value!;
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        var document = EndOfDayDocumentBuilder.Build(shown, cooperative?.ShortName ?? cooperative?.FullName ?? string.Empty, timeProvider.GetUtcNow());
        return documents.Write(fileFormat, $"BaoCaoCuoiNgay_{shown.Date:yyyyMMdd}", document);
    }
}
