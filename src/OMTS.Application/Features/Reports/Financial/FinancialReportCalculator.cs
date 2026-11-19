using System.Globalization;
using OMTS.Application.Common.Interfaces.Persistence;
using static OMTS.Application.Common.Interfaces.Persistence.FinancialEntryCodes;

namespace OMTS.Application.Features.Reports.Financial;

/// <summary>
/// The statement as pure functions: the columns of a period, the amounts put in the column of their date, deductions and costs signed negative, net revenue = revenue − deductions and
/// profit = net revenue − costs + other income.
/// </summary>
public static class FinancialReportCalculator
{
    private static readonly (string Code, string Section, int Sign)[] SourceLines =
    [
        (Revenue, "revenue", 1),
        (InvoiceDiscounts, "deduction", -1), (PriceCuts, "deduction", -1), (SalesReturns, "deduction", -1), (PaymentDiscounts, "deduction", -1),
        (InputUseCost, "cost", -1), (InputDisposalCost, "cost", -1), (LaborCost, "cost", -1), (OtherExpense, "cost", -1),
        (OtherIncome, "other-income", 1),
    ];

    public static IReadOnlyList<FinancialColumn> Columns(DateOnly from, DateOnly to, FinancialUnit unit)
    {
        var columns = new List<FinancialColumn>();
        for (var start = StartOf(from, unit); start <= to; start = Next(start, unit))
        {
            columns.Add(new FinancialColumn(KeyOf(start, unit), KeyOf(start, unit), start, Next(start, unit).AddDays(-1)));
        }

        return columns;
    }

    public static IReadOnlyList<FinancialRow> Rows(IReadOnlyList<FinancialEntry> entries, IReadOnlyList<FinancialColumn> columns)
    {
        decimal[] Sum(string code, int sign) => columns
            .Select(c => sign * entries.Where(e => e.Code == code && e.Date >= c.From && e.Date <= c.To).Sum(e => e.Amount))
            .ToArray();

        var rows = new List<FinancialRow>();
        var byColumn = new Dictionary<string, decimal[]>();
        FinancialRow Add(string code, string section, bool isTotal, decimal[] values)
        {
            var row = new FinancialRow(code, section, isTotal, values, values.Sum());
            rows.Add(row);
            byColumn[code] = values;
            return row;
        }

        decimal[] Plus(IEnumerable<string> codes) => columns.Select((_, i) => codes.Sum(c => byColumn[c][i])).ToArray();

        foreach (var (code, section, sign) in SourceLines)
        {
            if (code == InputUseCost)
            {
                Add("net-revenue", "net-revenue", true, Plus(SourceLines.Where(l => l.Section is "revenue" or "deduction").Select(l => l.Code)));
            }

            Add(code, section, false, Sum(code, sign));
        }

        Add("profit", "profit", true, Plus(SourceLines.Select(l => l.Code)));
        return rows;
    }

    public static DateOnly StartOf(DateOnly date, FinancialUnit unit) => unit switch
    {
        FinancialUnit.Year => new DateOnly(date.Year, 1, 1),
        FinancialUnit.Quarter => new DateOnly(date.Year, (date.Month - 1) / 3 * 3 + 1, 1),
        _ => new DateOnly(date.Year, date.Month, 1),
    };

    private static DateOnly Next(DateOnly start, FinancialUnit unit) => unit switch
    {
        FinancialUnit.Year => start.AddYears(1),
        FinancialUnit.Quarter => start.AddMonths(3),
        _ => start.AddMonths(1),
    };

    private static string KeyOf(DateOnly start, FinancialUnit unit) => unit switch
    {
        FinancialUnit.Year => start.Year.ToString(CultureInfo.InvariantCulture),
        FinancialUnit.Quarter => $"{start.Year}-Q{(start.Month - 1) / 3 + 1}",
        _ => start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
    };

    public static FinancialUnit? ParseUnit(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "month" => FinancialUnit.Month,
        "quarter" => FinancialUnit.Quarter,
        "year" => FinancialUnit.Year,
        _ => null,
    };
}
