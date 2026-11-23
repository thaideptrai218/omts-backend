using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// The period of the report as instants: <paramref name="From"/> up to but not including <paramref name="ToExclusive"/> (the end minute is included by the caller adding one minute).
/// <paramref name="ZoneIds"/> limits the invoices, the returns (by their invoice), the orders (by their zone or one of their invoices) and the lots (by their raft) when not null.
/// </summary>
public sealed record EndOfDayScope(DateTimeOffset From, DateTimeOffset ToExclusive, IReadOnlyCollection<Guid>? ZoneIds);

/// <summary>What the user chose for the sales; an empty list means "no limit".</summary>
public sealed record EndOfDaySalesCriteria(
    IReadOnlyCollection<Guid> CustomerIds, IReadOnlyCollection<Guid> ReceivedBy, IReadOnlyCollection<Guid> CreatedBy, IReadOnlyCollection<CustomerPaymentMethod> PaymentMethods,
    IReadOnlyCollection<SalesOrderSource> Channels, IReadOnlyCollection<SalesPaymentTerm> PaymentTerms);

/// <summary>
/// What limits the invoices and returns of the goods; an empty list means "no limit". The products and categories chosen are not here: they limit the lines only after the discount of each
/// invoice is shared over all its lines.
/// </summary>
public sealed record EndOfDayGoodsCriteria(IReadOnlyCollection<Guid> CustomerIds, IReadOnlyCollection<Guid> CreatedBy);

/// <summary>An order placed in the period; <paramref name="Deposits"/> is what customers paid on it in the period (payments that name the order and no invoice).</summary>
public sealed record EndOfDayOrderFact(
    Guid Id, string Code, DateTimeOffset At, Guid? CustomerId, string? CustomerName, string? ZoneName, decimal Kg, decimal Subtotal, decimal Discount, decimal Deposits);

/// <summary>
/// An invoice made in the period (not Cancelled). <paramref name="Discount"/> is the invoice discount plus its price cuts; <paramref name="PaidInPeriod"/> what payments recorded with it
/// brought in during the period; <paramref name="PaidTotal"/> what they brought in at all; <paramref name="ReturnedValue"/> the value of the sales return that stands on it, if any.
/// </summary>
public sealed record EndOfDayInvoiceFact(
    Guid Id, string Code, DateTimeOffset At, Guid? CustomerId, string? CustomerName, string ZoneName, decimal Kg, decimal Subtotal, decimal Discount, decimal OtherCharges, decimal Total,
    decimal PaidInPeriod, decimal PaidTotal, decimal ReturnedValue);

/// <summary>
/// A sales return that stands, recorded in the period; <paramref name="Value"/> is the total of its invoice and <paramref name="InvoiceOtherCharges"/> the other charges inside that total.
/// </summary>
public sealed record EndOfDayReturnFact(
    Guid Id, string Code, DateTimeOffset At, Guid InvoiceId, string InvoiceCode, Guid? CustomerId, string? CustomerName, string ZoneName, decimal Kg, decimal Value,
    decimal InvoiceOtherCharges = 0m);

public sealed record EndOfDaySalesFacts(IReadOnlyList<EndOfDayOrderFact> Orders, IReadOnlyList<EndOfDayInvoiceFact> Invoices, IReadOnlyList<EndOfDayReturnFact> Returns);

/// <summary>One invoice line with what is needed to share the discount of its invoice over its lines.</summary>
public sealed record EndOfDayLineFact(
    Guid Id, Guid InvoiceId, string InvoiceCode, DateTimeOffset InvoicedAt, string? CustomerName, int LineNumber, Guid ProductId, string ProductCode, string ProductName,
    Guid? CategoryId, decimal Kg, decimal Amount, decimal InvoiceDiscount);

/// <summary>A line of a sales return that stands: the invoice line it sends back (all its kg) and the return it belongs to.</summary>
public sealed record EndOfDayReturnedLineFact(
    Guid ReturnId, string ReturnCode, DateTimeOffset At, Guid InvoiceId, string InvoiceCode, string? CustomerName, Guid InvoiceLineId, decimal ReturnedKg);

/// <summary>
/// The lines of the invoices of the period (<paramref name="Lines"/>), the returned lines of the returns of the period and every line of the invoices those returns belong to
/// (<paramref name="ReturnedInvoiceLines"/>, the ones already in <paramref name="Lines"/> left out), so the discount of each invoice can be shared over all its lines.
/// </summary>
public sealed record EndOfDayGoodsFacts(
    IReadOnlyList<EndOfDayLineFact> Lines, IReadOnlyList<EndOfDayReturnedLineFact> ReturnedLines, IReadOnlyList<EndOfDayLineFact> ReturnedInvoiceLines);

/// <summary>A harvest lot weighed in the period, in the zone of its raft.</summary>
public sealed record EndOfDayLot(Guid Id, string LotCode, Guid ZoneId, string ZoneName, decimal WeighedKg, DateTimeOffset WeighedAt);

/// <summary>A product category with its parent, so a chosen category can include what is below it.</summary>
public sealed record EndOfDayCategory(Guid Id, string Name, Guid? ParentId);

/// <summary>The sales and goods of a period (the money comes from the cash book query). Read only; each method is a fixed number of commands.</summary>
public interface IEndOfDayReportQuery
{
    /// <summary>The orders and the invoices of the period, the returns recorded in it, and the money brought in with them in it. Orders are left out when a payment term is chosen (they have none), returns when a payment method is chosen.</summary>
    Task<EndOfDaySalesFacts> SalesAsync(EndOfDayScope scope, EndOfDaySalesCriteria criteria, CancellationToken cancellationToken);

    /// <summary>The invoice lines of the period and the lines sent back by the returns of the period.</summary>
    Task<EndOfDayGoodsFacts> GoodsAsync(EndOfDayScope scope, EndOfDayGoodsCriteria criteria, CancellationToken cancellationToken);

    /// <summary>The lots weighed in the period, in the zones of the scope.</summary>
    Task<IReadOnlyList<EndOfDayLot>> LotsAsync(EndOfDayScope scope, CancellationToken cancellationToken);

    /// <summary>Every product category.</summary>
    Task<IReadOnlyList<EndOfDayCategory>> CategoriesAsync(CancellationToken cancellationToken);
}
