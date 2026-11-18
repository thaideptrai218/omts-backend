using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Interfaces.Addresses;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AdministrativeUnits;

namespace OMTS.Application.Features.TaxLookup;

/// <summary>A province or ward of the administrative units list.</summary>
public sealed record AddressUnitRef(int Code, string Name);

/// <summary>
/// A company of the tax authority's registry with its address split into the codes of the administrative units list.
/// </summary>
/// <param name="Address">The address as the registry writes it.</param>
/// <param name="Province">Null when the last part of the address names no province of the list.</param>
/// <param name="Ward">Null when no ward of that province is named in the address.</param>
/// <param name="Street">What stands before the ward; the whole address when no province was recognised.</param>
public sealed record TaxLookupResult(
    string TaxCode,
    string Name,
    string? ShortName,
    string Address,
    string Status,
    AddressUnitRef? Province,
    AddressUnitRef? Ward,
    string? Street);

/// <summary>
/// Looks a company up by its tax code in the tax authority's registry (through <see cref="ITaxRegistry"/>) for the invoice form of a customer or a supplier.
/// Only the tax code leaves OMTS. A company found is kept 24 hours per tax code; a miss or a failure is not kept. The limit per account is the API's
/// (<c>TaxLookups</c> rate limit policy).
/// </summary>
public sealed partial class TaxLookupService(ITaxRegistry registry, AdministrativeUnitService units, IMemoryCache cache)
{
    public static readonly TimeSpan CacheTime = TimeSpan.FromHours(24);

    /// <summary>10 digits, or 10 digits, a hyphen and 3 digits (a branch).</summary>
    public static bool IsTaxCode(string? value) => value is not null && TaxCodeFormat().IsMatch(value);

    public async Task<Result<TaxLookupResult>> LookupAsync(string? taxCode, CancellationToken cancellationToken)
    {
        var code = taxCode?.Trim();
        if (!IsTaxCode(code))
        {
            return AppError.Invalid("Tax code");
        }

        var key = $"tax-lookup:{code}";
        if (!cache.TryGetValue(key, out TaxRegistryCompany? company) || company is null)
        {
            var found = await registry.FindAsync(code!, cancellationToken);
            switch (found.Outcome)
            {
                case TaxRegistryOutcome.NotFound:
                    return new AppError(ErrorKind.NotFound, UserMessages.TaxCodeNotFound);
                case TaxRegistryOutcome.Found when found.Company is { } read:
                    company = read;
                    cache.Set(key, company, CacheTime);
                    break;
                default:
                    return new AppError(ErrorKind.ServiceUnavailable, UserMessages.TaxRegistryNotResponding);
            }
        }

        return await WithSplitAddressAsync(company, cancellationToken);
    }

    private async Task<TaxLookupResult> WithSplitAddressAsync(TaxRegistryCompany company, CancellationToken cancellationToken)
    {
        string? street = company.Address;
        AddressUnitRef? province = null;
        AddressUnitRef? ward = null;
        if (await units.TryProvincesAsync(cancellationToken) is { } provinces
            && AddressSplitter.SplitProvince(company.Address, provinces) is { } split
            && await units.TryWardsAsync(split.Province.Code, cancellationToken) is { } wards)
        {
            var result = AddressSplitter.SplitWard(split, wards);
            province = new AddressUnitRef(result.Province.Code, result.Province.Name);
            ward = result.Ward is { } matched ? new AddressUnitRef(matched.Code, matched.Name) : null;
            street = result.Street;
        }

        return new TaxLookupResult(
            company.TaxCode, company.Name, company.ShortName, company.Address, company.Status, province, ward, street);
    }

    [GeneratedRegex(@"^\d{10}(-\d{3})?$")]
    private static partial Regex TaxCodeFormat();
}
