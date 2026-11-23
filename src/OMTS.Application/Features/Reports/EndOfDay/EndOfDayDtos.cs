namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>What the report is about: the sales, the money, the goods, or a short summary of the three.</summary>
public enum EndOfDayConcern
{
    Sales,
    Money,
    Goods,
    Summary,
}

/// <summary>
/// The query of the report and its export. <c>Concern</c> is sales (default), money, goods or summary. The period is <c>From</c> to <c>To</c>, local Vietnam times <c>yyyy-MM-ddTHH:mm</c>, both
/// included (<c>To</c> up to the end of its minute): today 00:00 to 23:59 when none is given; <c>Date</c> (an older way to ask) is the whole of that day. <c>ZoneIds</c> (repeated) limits the sales,
/// the goods and the lots (money has no zone). The other lists are repeated parameters that belong to one concern and are left alone by the others: sales <c>CustomerIds</c>, <c>ReceivedBy</c>,
/// <c>CreatedBy</c>, <c>PaymentMethods</c>, <c>Channels</c>, <c>PaymentTerms</c>; money <c>Funds</c>, <c>Sources</c>, <c>TypeIds</c>, <c>PartyKinds</c>, <c>CreatedBy</c>; goods <c>ProductIds</c>,
/// <c>CategoryIds</c>, <c>CustomerIds</c>, <c>CreatedBy</c>; summary <c>CreatedBy</c>.
/// </summary>
public sealed record EndOfDayQuery(
    DateOnly? Date = null,
    string? Concern = null,
    string? From = null,
    string? To = null,
    Guid[]? ZoneIds = null,
    Guid[]? CustomerIds = null,
    Guid[]? ReceivedBy = null,
    Guid[]? CreatedBy = null,
    string[]? PaymentMethods = null,
    string[]? Channels = null,
    string[]? PaymentTerms = null,
    string[]? Funds = null,
    string[]? Sources = null,
    Guid[]? TypeIds = null,
    string[]? PartyKinds = null,
    Guid[]? ProductIds = null,
    Guid[]? CategoryIds = null);

/// <summary>
/// The choices that were checked and applied, with the names to show them: the lists a concern does not use are empty. <c>People</c> holds the people of <c>ReceivedBy</c> and <c>CreatedBy</c>
/// together. The enum lists hold the names of the values (PaymentMethods: Cash, VietQrInvoice, OtherTransfer, EWallet; Channels: App, Phone, AtPort; PaymentTerms: PayNow, PayLater;
/// Funds: Cash, Bank, EWallet; Sources: CustomerPayment, CustomerPaidBack, SupplierPayment, SupplierPaidBack, CashVoucher; PartyKinds: Customer, Supplier, Employee, Other).
/// </summary>
public sealed record EndOfDayChoices(
    IReadOnlyList<ReportChoice> Zones,
    IReadOnlyList<ReportChoice> Customers,
    IReadOnlyList<ReportChoice> Products,
    IReadOnlyList<ReportChoice> Categories,
    IReadOnlyList<ReportChoice> People,
    IReadOnlyList<ReportChoice> ReceivedBy,
    IReadOnlyList<ReportChoice> CreatedBy,
    IReadOnlyList<ReportChoice> Types,
    IReadOnlyList<string> PaymentMethods,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> PaymentTerms,
    IReadOnlyList<string> Funds,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> PartyKinds);

public sealed record EndOfDayCustomer(Guid Id, string Name);

/// <summary>
/// One document of the sales. <c>Revenue</c> is the goods before the discount (the subtotal); <c>Discount</c> the invoice discount plus its price cuts; <c>Paid</c> what was received with it in
/// the period (an order: the deposits); <c>Debt</c> what is still owed on an invoice, 0 for an order or a return. A return carries negative kg and revenue. <c>ZoneName</c> is null for an
/// order that has no zone.
/// </summary>
public sealed record EndOfDaySalesRow(
    Guid Id, string Code, DateTimeOffset At, EndOfDayCustomer? Customer, string? ZoneName, decimal QuantityKg, decimal Revenue, decimal Discount, decimal OtherCharges, decimal Paid, decimal Debt);

/// <summary>Kind is Order, Invoice or Return; the figures add up the rows of the group.</summary>
public sealed record EndOfDaySalesGroup(
    string Kind, int Count, decimal QuantityKg, decimal Revenue, decimal Discount, decimal OtherCharges, decimal Paid, decimal Debt, IReadOnlyList<EndOfDaySalesRow> Rows);

/// <summary>
/// The invoices and the returns together (an order is not revenue): <c>Revenue</c> = invoice revenue minus the value of the returns, <c>NetRevenue</c> = <c>Revenue</c> minus <c>Discount</c>.
/// <c>Paid</c> also holds the deposits received on orders; <c>Debt</c> is the debt of the invoices.
/// </summary>
public sealed record EndOfDaySalesTotals(decimal QuantityKg, decimal Revenue, decimal Discount, decimal OtherCharges, decimal Paid, decimal Debt, decimal NetRevenue);

/// <summary>The sales of the period: always the three groups Order, Invoice, Return (a group may have no rows).</summary>
public sealed record EndOfDaySales(IReadOnlyList<EndOfDaySalesGroup> Groups, EndOfDaySalesTotals Totals);

/// <summary>
/// One line of the cash book of the period. <c>Source</c> is CustomerPayment, CustomerPaidBack, SupplierPayment, SupplierPaidBack or CashVoucher; <c>Direction</c> is Receipt or Payment;
/// <c>Fund</c> is Cash, Bank or EWallet; <c>PartyKind</c> is Customer, Supplier, Employee or Other.
/// </summary>
public sealed record EndOfDayMoneyEntry(
    string Source, string Code, DateTimeOffset At, string Direction, string Fund, string? TypeName, string? PartyKind, string? PartyName, decimal Amount, string? Note, string? CreatedByName);

public sealed record EndOfDayFundTotals(string Fund, decimal Receipts, decimal Payments, decimal Net);

public sealed record EndOfDayMoneyTotals(decimal Receipts, decimal Payments, decimal Net);

/// <summary>The recorded cash book lines of the period (the same lines and sums as the cash book), by fund. Empty for a user limited to some zones.</summary>
public sealed record EndOfDayMoney(IReadOnlyList<EndOfDayMoneyEntry> Entries, IReadOnlyList<EndOfDayFundTotals> ByFund, EndOfDayMoneyTotals Totals);

/// <summary>One document of a product: an invoice that sold it or a return that sent it back. <c>Value</c> is after the share of the invoice discount.</summary>
public sealed record EndOfDayGoodsDetail(Guid DocumentId, string Code, string Kind, DateTimeOffset At, string? CustomerName, decimal Kg, decimal Value);

/// <summary>
/// A product of the period. <c>Revenue</c> and <c>ReturnValue</c> are after the share of the invoice discount over the lines of the invoice (by amount, to the whole dong, the rest on the last
/// line); <c>NetRevenue</c> = <c>Revenue</c> - <c>ReturnValue</c>. <c>Unit</c> is always "kg".
/// </summary>
public sealed record EndOfDayProductRow(
    Guid ProductId, string Code, string Name, string Unit, decimal SoldKg, decimal Revenue, decimal ReturnedKg, decimal ReturnValue, decimal NetRevenue, IReadOnlyList<EndOfDayGoodsDetail> Details);

public sealed record EndOfDayLotRow(Guid Id, string LotCode, string ZoneName, decimal WeighedKg, DateTimeOffset WeighedAt);

public sealed record EndOfDayGoodsTotals(decimal SoldKg, decimal Revenue, decimal ReturnedKg, decimal ReturnValue, decimal NetRevenue, int LotCount, decimal WeighedKg);

/// <summary>The kg and revenue per product of the period, and the lots weighed in it.</summary>
public sealed record EndOfDayGoods(IReadOnlyList<EndOfDayProductRow> Products, IReadOnlyList<EndOfDayLotRow> Lots, EndOfDayGoodsTotals Totals);

/// <summary>The sales in short: <c>OrderValue</c> is the orders after their discount, <c>Revenue</c> is the invoices before discount, <c>NetRevenue</c> = <c>Revenue</c> - <c>Discount</c> - <c>ReturnValue</c>, <c>Paid</c> includes <c>Deposits</c>.</summary>
public sealed record EndOfDaySalesSummary(
    int OrderCount, decimal OrderValue, decimal Deposits, int InvoiceCount, decimal Revenue, decimal Discount, int ReturnCount, decimal ReturnValue, decimal NetRevenue, decimal Paid, decimal Debt);

public sealed record EndOfDayMoneySummary(IReadOnlyList<EndOfDayFundTotals> ByFund, EndOfDayMoneyTotals Totals);

public sealed record EndOfDayGoodsSummary(int ProductCount, decimal SoldKg, decimal ReturnedKg, int LotCount, decimal WeighedKg);

public sealed record EndOfDaySummary(EndOfDaySalesSummary Sales, EndOfDayMoneySummary Money, EndOfDayGoodsSummary Goods);

/// <summary>
/// The report worked out when it is shown. <c>Concern</c> is sales, money, goods or summary and only the block of that concern is set. <c>From</c> and <c>To</c> are the period as
/// <c>yyyy-MM-ddTHH:mm</c>, Vietnam time. <see cref="Message"/> is MSG-GEN-10 when the period has nothing for the concern.
/// </summary>
public sealed record EndOfDayReport(
    DateTimeOffset ComputedAt,
    string Concern,
    string From,
    string To,
    EndOfDayChoices Filters,
    EndOfDaySales? Sales,
    EndOfDayMoney? Money,
    EndOfDayGoods? Goods,
    EndOfDaySummary? Summary,
    string? Message,
    string? MessageCode);
