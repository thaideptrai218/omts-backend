using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Reports.Documents;

namespace OMTS.Application.Features.Labor.Costs;

/// <summary>The labour cost by raft as a printable file: <c>ChiPhiNhanCong_{from}_{to}</c> as .xlsx (default) or .pdf, with the same selection and the same right (PAYROLL Export) as the screen. Changes nothing.</summary>
public sealed class LaborCostExportService(LaborCostService costs, ReportDocumentExport documents, ICooperativeRepository cooperatives, TimeProvider timeProvider)
{
    public async Task<Result<ExportedFile>> ExportAsync(LaborCostQuery query, string? format, CancellationToken cancellationToken)
    {
        var (fileFormat, formatOk) = ReportDocumentExport.ParseFormat(format);
        if (!formatOk)
        {
            return AppError.Invalid(ReportDocumentExport.FormatField);
        }

        var found = await costs.ByRaftAsync(query, cancellationToken);
        if (!found.IsSuccess)
        {
            return found.Error!;
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        var document = LaborCostDocumentBuilder.Build(found.Value!, cooperative?.ShortName ?? cooperative?.FullName ?? string.Empty, timeProvider.GetUtcNow());
        return documents.Write(fileFormat, $"ChiPhiNhanCong_{query.From:yyyyMMdd}_{query.To:yyyyMMdd}", document);
    }
}