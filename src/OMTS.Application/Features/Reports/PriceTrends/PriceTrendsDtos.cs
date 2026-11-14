namespace OMTS.Application.Features.Reports.PriceTrends;

/// <summary>
/// The query of the three REPORT-02 calls (the report, the lines of a product and period, the export). Dates are Vietnam dates: the current month when none is given. <c>Unit</c> is day
/// (default), week or month. <c>ProductId</c> and <c>PeriodKey</c> (the <c>key</c> of a period of the report) are for the lines call only.
/// </summary>
public sealed record PriceTrendsQuery(
    DateOnly? From = null, DateOnly? To = null, string? Unit = null, Guid[]? ProductIds = null, Guid[]? ZoneIds = null, Guid? ProductId = null, string? PeriodKey = null);

/// <summary>The choices that were checked and applied, as the screen and the file show them.</summary>
public sealed record PriceTrendsChoices(IReadOnlyList<ReportChoice> Zones, IReadOnlyList<ReportChoice> Products);

/// <summary>One day, ISO week or month of a product (BR-REPORT-02); <see cref="Key"/> is its first day (yyyy-MM-dd). Prices are VND per kg, not rounded; <see cref="AveragePrice"/> is weighted by kg.</summary>
public sealed record PriceTrendPeriod(string Key, string Label, decimal SoldKg, decimal AveragePrice, decimal LowestPrice, decimal HighestPrice);

/// <summary>The periods of one product in time order; a period without a counted line is not listed.</summary>
public sealed record PriceTrendSeries(Guid ProductId, string ProductName, IReadOnlyList<PriceTrendPeriod> Periods);

/// <summary>REPORT-02 step 3: worked out when it is shown. <see cref="Message"/> is MSG-GEN-10 when no line matches (REPORT-02.1); the series are then empty.</summary>
public sealed record PriceTrendsReport(
    DateTimeOffset ComputedAt, DateOnly From, DateOnly To, string Unit, PriceTrendsChoices Filters, IReadOnlyList<PriceTrendSeries> Series, string? Message, string? MessageCode);

public sealed record PriceTrendCustomer(Guid Id, string Name);

/// <summary>REPORT-02.2: a counted line; the customer is null for a walk-in customer.</summary>
public sealed record PriceTrendLineRow(Guid InvoiceId, string InvoiceCode, DateOnly Date, PriceTrendCustomer? Customer, decimal Kg, decimal UnitPrice, decimal Amount);

/// <summary>The lines of one product and period, newest first; <see cref="Message"/> is MSG-GEN-10 when there are none.</summary>
public sealed record PriceTrendLineList(
    DateOnly From, DateOnly To, string Unit, Guid ProductId, string PeriodKey, IReadOnlyList<PriceTrendLineRow> Lines, string? Message, string? MessageCode);
