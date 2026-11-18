namespace OMTS.Application.Common.Interfaces.Addresses;

public enum TaxRegistryOutcome
{
    Found,
    NotFound,

    /// <summary>The service did not answer, refused the call or gave an answer that could not be read.</summary>
    Unavailable,
}

/// <param name="TaxCode">As the registry holds it.</param>
/// <param name="Address">One line, as the registry gives it: "Số 5, Phố Nguyễn Hữu An, Phường Cầu Giấy, TP Hà Nội".</param>
/// <param name="Status">e.g. "NNT đang hoạt động".</param>
public sealed record TaxRegistryCompany(string TaxCode, string Name, string? ShortName, string Address, string Status);

public sealed record TaxRegistryLookup(TaxRegistryOutcome Outcome, TaxRegistryCompany? Company = null)
{
    public static readonly TaxRegistryLookup NotFound = new(TaxRegistryOutcome.NotFound);
    public static readonly TaxRegistryLookup Unavailable = new(TaxRegistryOutcome.Unavailable);
}

/// <summary>
/// The business registry of the tax authority as an outside service gives it. Only the tax code is sent out. Failures are returned as
/// <see cref="TaxRegistryOutcome.Unavailable"/>, never thrown.
/// </summary>
public interface ITaxRegistry
{
    Task<TaxRegistryLookup> FindAsync(string taxCode, CancellationToken cancellationToken);
}
