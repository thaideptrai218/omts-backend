using OMTS.Domain.Entities.Sales;

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

    /// <summary>
    /// Profiles not merged, by name or phone number (contains), ordered by name, with their accounts. <paramref name="phoneNumber"/>
    /// is the search read as a whole number, normalized, when it reads as one: also a match.
    /// </summary>
    Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(string? search, string? phoneNumber, int page, int pageSize, CancellationToken cancellationToken);

    void Add(Customer customer);
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
