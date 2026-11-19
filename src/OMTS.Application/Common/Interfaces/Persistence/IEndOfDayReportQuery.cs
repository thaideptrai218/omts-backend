namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>An invoice of the day: <paramref name="PaidNow"/> is what payments recorded with it that day brought in (not cancelled, not waiting for a transfer).</summary>
public sealed record EndOfDayInvoice(
    Guid Id, string Code, DateTimeOffset InvoicedAt, Guid? CustomerId, string? CustomerName, Guid ZoneId, string ZoneName, decimal Kg, decimal Total, decimal PaidNow);

/// <summary>A sales return that stands, dated the day; <paramref name="Value"/> is the total of its invoice.</summary>
public sealed record EndOfDayReturn(Guid Id, string Code, Guid InvoiceId, string InvoiceCode, string? CustomerName, Guid ZoneId, string ZoneName, decimal Value);

/// <summary>The kg of a product on the invoices of the day.</summary>
public sealed record EndOfDayProduct(Guid ProductId, string Code, string Name, decimal Kg);

/// <summary>A harvest lot weighed on the day, in the zone of its raft.</summary>
public sealed record EndOfDayLot(Guid Id, string LotCode, Guid ZoneId, string ZoneName, decimal WeighedKg, DateTimeOffset WeighedAt);

public sealed record EndOfDayFacts(
    IReadOnlyList<EndOfDayInvoice> Invoices, IReadOnlyList<EndOfDayReturn> Returns, IReadOnlyList<EndOfDayProduct> Products, IReadOnlyList<EndOfDayLot> Lots);

/// <summary>The sales, goods and lots of one business day (the money comes from the cash book query). Read only.</summary>
public interface IEndOfDayReportQuery
{
    /// <param name="zoneIds">Only these zones when not null (invoices, returns by their invoice, lots by their raft).</param>
    /// <param name="createdBy">Only invoices and returns recorded by this account; lots are not limited by it.</param>
    Task<EndOfDayFacts> FactsAsync(DateOnly date, IReadOnlyCollection<Guid>? zoneIds, Guid? createdBy, CancellationToken cancellationToken);
}
