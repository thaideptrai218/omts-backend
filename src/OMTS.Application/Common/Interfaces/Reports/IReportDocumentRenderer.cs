using OMTS.Application.Features.Reports.Documents;

namespace OMTS.Application.Common.Interfaces.Reports;

/// <summary>The file formats a <see cref="ReportDocument"/> can be rendered to.</summary>
public enum ReportFileFormat
{
    Xlsx,
    Pdf,
}

/// <summary>Renders a <see cref="ReportDocument"/> to one file format; throws when the file cannot be generated.</summary>
public interface IReportDocumentRenderer
{
    ReportFileFormat Format { get; }

    string ContentType { get; }

    string Extension { get; }

    byte[] Render(ReportDocument document);
}
