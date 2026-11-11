using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ICustomerRepository
{
    /// <summary>Offline profiles (no account, not merged) with this phone number.</summary>
    Task<IReadOnlyList<Customer>> GetOfflineByPhoneAsync(string phoneNumber, CancellationToken cancellationToken);

    Task<Customer?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>The profile with its linked trader account, tracked; merged profiles included.</summary>
    Task<Customer?> GetForEditAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>Another profile that has this phone number and is not merged (BR-SALE-04).</summary>
    Task<Customer?> FindByPhoneAsync(string phoneNumber, Guid? exceptCustomerId, CancellationToken cancellationToken);

    /// <summary>Another profile with this code regardless of letter case (merged ones included: their code is never given again).</summary>
    Task<Customer?> FindByCodeAsync(string code, Guid? exceptCustomerId, CancellationToken cancellationToken);

    /// <summary>The numbers among <paramref name="phoneNumbers"/> that a profile that is not merged already has.</summary>
    Task<IReadOnlySet<string>> ExistingPhonesAsync(IReadOnlyCollection<string> phoneNumbers, CancellationToken cancellationToken);

    void Add(Customer customer);

    void Remove(Customer customer);
}

public enum CustomerSort
{
    Code,
    Name,
    Phone,
    Type,
    Province,
    CreatedAt,
    Status,
    TotalSales,
    NetSales,
    CurrentDebt,
    LastTransaction,
}

/// <summary>The filters of the customer list and export. A date range is on the business date; ranges are inclusive.</summary>
/// <param name="Search">Text in the name, code or phone number.</param>
/// <param name="PhoneSearch">The search read as a whole phone number, normalized: also a match.</param>
/// <param name="BirthdayFrom">With <paramref name="BirthdayTo"/>: customers whose birthday (month and day, the year is ignored) falls in the range; a range that ends before it starts wraps over the new year.</param>
/// <param name="SalesFrom">With <paramref name="SalesTo"/>: the period the total sales and the net sales are summed over; all time when neither is given.</param>
/// <param name="StatusId">Null: every status.</param>
/// <param name="OnlyIds">Null: no restriction; otherwise only these profiles (the ones over the debt limit).</param>
public sealed record CustomerSearchFilter(
    string? Search = null,
    string? PhoneSearch = null,
    CustomerType? Type = null,
    Gender? Gender = null,
    DateOnly? BirthdayFrom = null,
    DateOnly? BirthdayTo = null,
    DateOnly? LastTransactionFrom = null,
    DateOnly? LastTransactionTo = null,
    decimal? TotalSalesFrom = null,
    decimal? TotalSalesTo = null,
    DateOnly? SalesFrom = null,
    DateOnly? SalesTo = null,
    decimal? CurrentDebtFrom = null,
    decimal? CurrentDebtTo = null,
    string? Province = null,
    int? StatusId = null,
    Guid? CreatedById = null,
    DateOnly? CreatedFrom = null,
    DateOnly? CreatedTo = null,
    CustomerSort Sort = CustomerSort.Code,
    bool Descending = true,
    IReadOnlyCollection<Guid>? OnlyIds = null);

/// <summary>A profile with its figures: the sales in the period (before and after the price cuts), the debt now and the time of the last sale.</summary>
public sealed record CustomerFigureRow(Customer Customer, decimal TotalSales, decimal NetSales, decimal Debt, DateTimeOffset? LastTransactionAt);

/// <summary>The sums over every customer that matches, not only the page.</summary>
public sealed record CustomerListSums(decimal Debt, decimal TotalSales, decimal NetSales);

public interface ICustomerListQuery
{
    /// <summary>One page, and the number and sums of every match. The figures are summed by SQL, so they are the ones every other screen shows.</summary>
    Task<(IReadOnlyList<CustomerFigureRow> Items, int Total, CustomerListSums Sums)> SearchAsync(
        CustomerSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Every match, in order (the export).</summary>
    Task<IReadOnlyList<CustomerFigureRow>> ListAsync(CustomerSearchFilter filter, CancellationToken cancellationToken);
}

/// <summary>One column that names a customer profile, as listed in the one place that lists them.</summary>
/// <param name="Name">What the rows are, for people ("Merged customer profiles").</param>
/// <param name="RowType">The entity that holds the column.</param>
/// <param name="Column">The property that holds the customer id.</param>
public sealed record CustomerReferenceColumn(string Name, Type RowType, string Column);

/// <summary>How many rows of a reference a profile has.</summary>
public sealed record CustomerReferenceCount(string Name, int Count);

/// <summary>The rows of a reference that moved to the kept profile.</summary>
public sealed record MovedCustomerReference(string Name, IReadOnlyList<Guid> RowIds);

/// <summary>
/// Every row that refers to a customer profile (SALE-04.3): a merge re-points them to the kept profile, so all later
/// queries are a plain <c>CustomerId = x</c>. Rows are changed in the current unit of work, saved with the merge.
/// </summary>
public interface ICustomerReferences
{
    IReadOnlyList<CustomerReferenceColumn> Columns { get; }

    /// <summary>Per reference, how many rows name the profile.</summary>
    Task<IReadOnlyList<CustomerReferenceCount>> CountAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>Points every row that names <paramref name="fromCustomerId"/> at <paramref name="toCustomerId"/>; references with no row are left out.</summary>
    Task<IReadOnlyList<MovedCustomerReference>> RepointAsync(Guid fromCustomerId, Guid toCustomerId, CancellationToken cancellationToken);
}
