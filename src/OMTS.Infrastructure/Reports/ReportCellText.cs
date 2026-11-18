using System.Globalization;
using OMTS.Application.Features.Reports.Documents;

namespace OMTS.Infrastructure.Reports;

/// <summary>A cell value as text for the PDF: thousand separators with commas like the KiotViet reports (9,163,739), kg with two decimals, null as empty.</summary>
internal static class ReportCellText
{
    public static string Format(object? value, ReportCellFormat format) => value switch
    {
        null => string.Empty,
        decimal number => Number(number, format),
        int number => Number(number, format),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Number(decimal number, ReportCellFormat format) =>
        number.ToString(format == ReportCellFormat.Kilogram ? "#,##0.00" : "#,##0", CultureInfo.InvariantCulture);
}
