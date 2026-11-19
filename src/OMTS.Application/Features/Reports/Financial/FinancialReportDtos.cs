namespace OMTS.Application.Features.Reports.Financial;

/// <summary>The width of a column of the financial report.</summary>
public enum FinancialUnit
{
    Month,
    Quarter,
    Year,
}

/// <summary>The query of the report and its export. Dates are Vietnam dates, the current year (1 January to today) when none is given. <c>Unit</c> is month (default), quarter or year.</summary>
public sealed record FinancialReportQuery(DateOnly? From = null, DateOnly? To = null, string? Unit = null);

/// <summary>A column: <c>Key</c> is 2026-11, 2026-Q4 or 2026; <c>From</c>/<c>To</c> are the whole bucket (the period asked for may cut the first and the last).</summary>
public sealed record FinancialColumn(string Key, string Label, DateOnly From, DateOnly To);

/// <summary>
/// A line of the statement. <c>Code</c> names it for the client (revenue, invoice-discounts, price-cuts, sales-returns, payment-discounts, net-revenue, input-use-cost, input-disposal-cost,
/// labor-cost, other-expense, other-income, profit). <c>Section</c> is revenue, deduction, net-revenue, cost, other-income or profit; <c>IsTotal</c> marks net-revenue and profit. <c>Values</c> are signed as
/// they act on the result (deductions and costs negative), one per column in order; <c>Total</c> adds them. Net revenue = the lines above it; profit = every line.
/// </summary>
public sealed record FinancialRow(string Code, string Section, bool IsTotal, IReadOnlyList<decimal> Values, decimal Total);

/// <summary>
/// The business result of the period (KiotViet "Tài chính", no VAT). Costs are those of the period (inputs issued for use or disposal, net pay of the sheets finalized for the expense month, other
/// expense vouchers), not a cost of goods per kg; purchases are not counted, to avoid counting an input twice (when bought and when used). <see cref="Message"/> is MSG-GEN-10 when every figure is 0.
/// </summary>
public sealed record FinancialReport(
    DateTimeOffset ComputedAt, DateOnly From, DateOnly To, string Unit, IReadOnlyList<FinancialColumn> Columns, IReadOnlyList<FinancialRow> Rows, string? Message, string? MessageCode);
