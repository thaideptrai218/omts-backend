namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>
/// The query of the report and its export. <c>Date</c> is a Vietnam date, today when none is given. <c>ZoneIds</c> (repeated) limits the sales and the lots (money has no zone, decision D1);
/// <c>Method</c> is Cash, Bank or EWallet and limits the money block; <c>CreatedBy</c> is the account that recorded the invoice, return or voucher.
/// </summary>
public sealed record EndOfDayQuery(DateOnly? Date = null, Guid[]? ZoneIds = null, string? Method = null, Guid? CreatedBy = null);

/// <summary>The choices that were checked and applied; <c>Method</c> is "all", "Cash", "Bank" or "EWallet".</summary>
public sealed record EndOfDayChoices(IReadOnlyList<ReportChoice> Zones, string Method, Guid? CreatedBy);

public sealed record EndOfDayCustomer(Guid Id, string Name);

/// <summary><c>PaidNow</c> is what was paid with the invoice that day; <c>InvoicedAt</c> carries the time (UTC+7).</summary>
public sealed record EndOfDayInvoiceRow(Guid Id, string Code, DateTimeOffset InvoicedAt, EndOfDayCustomer? Customer, string ZoneName, decimal Kg, decimal Total, decimal PaidNow);

public sealed record EndOfDayReturnRow(Guid Id, string Code, Guid InvoiceId, string InvoiceCode, string? CustomerName, string ZoneName, decimal Value);

/// <summary><c>NetRevenue</c> = <c>Revenue − ReturnValue</c>; <c>Kg</c> is the invoiced kg.</summary>
public sealed record EndOfDaySalesTotals(int InvoiceCount, decimal Kg, decimal Revenue, decimal PaidNow, int ReturnCount, decimal ReturnValue, decimal NetRevenue);

/// <summary>Block 1: the invoices of the day (not Cancelled) and the sales returns that stand, dated the day.</summary>
public sealed record EndOfDaySales(IReadOnlyList<EndOfDayInvoiceRow> Invoices, IReadOnlyList<EndOfDayReturnRow> Returns, EndOfDaySalesTotals Totals);

/// <summary>
/// One line of the cash book of the day. <c>Source</c> is CustomerPayment, CustomerPaidBack, SupplierPayment, SupplierPaidBack or CashVoucher; <c>Direction</c> is Receipt or Payment;
/// <c>Fund</c> is Cash, Bank or EWallet.
/// </summary>
public sealed record EndOfDayMoneyEntry(string Source, string Code, DateTimeOffset OccurredAt, string Direction, string Fund, string? PartyName, decimal Amount, string? Note);

public sealed record EndOfDayFundTotals(string Fund, decimal Receipts, decimal Payments, decimal Net);

public sealed record EndOfDayMoneyTotals(decimal Receipts, decimal Payments, decimal Net);

/// <summary>Block 2: the recorded cash book lines of the day (the same lines and sums as the cash book), by fund. Empty for a user limited to some zones.</summary>
public sealed record EndOfDayMoney(IReadOnlyList<EndOfDayMoneyEntry> Entries, IReadOnlyList<EndOfDayFundTotals> ByFund, EndOfDayMoneyTotals Totals);

public sealed record EndOfDayProductRow(Guid ProductId, string Code, string Name, decimal Kg);

public sealed record EndOfDayLotRow(Guid Id, string LotCode, string ZoneName, decimal WeighedKg, DateTimeOffset WeighedAt);

public sealed record EndOfDayGoodsTotals(decimal SoldKg, int LotCount, decimal WeighedKg);

/// <summary>Block 3: the kg sold per product that day, and the lots weighed that day.</summary>
public sealed record EndOfDayGoods(IReadOnlyList<EndOfDayProductRow> Products, IReadOnlyList<EndOfDayLotRow> Lots, EndOfDayGoodsTotals Totals);

/// <summary>The report worked out when it is shown; <see cref="Message"/> is MSG-GEN-10 when the day has nothing in any block.</summary>
public sealed record EndOfDayReport(
    DateTimeOffset ComputedAt, DateOnly Date, EndOfDayChoices Filters, EndOfDaySales Sales, EndOfDayMoney Money, EndOfDayGoods Goods, string? Message, string? MessageCode);
