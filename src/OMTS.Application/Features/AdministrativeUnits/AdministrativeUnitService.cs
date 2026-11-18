using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Interfaces.Addresses;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.AdministrativeUnits;

/// <summary>The codes an entry of province and ward names matched; null for no match.</summary>
public sealed record AddressNameMatch(int? ProvinceCode, int? WardCode);

public enum AddressCheckOutcome
{
    /// <summary>The codes are in the list and the ward belongs to the province.</summary>
    Valid,

    /// <summary>The list could not be read now: the codes are taken as sent.</summary>
    NotChecked,

    UnknownProvince,

    /// <summary>The ward is not in the list or not in the province.</summary>
    UnknownWard,
}

/// <param name="ProvinceName">The name of the list for a valid code; null otherwise.</param>
public sealed record AddressCheck(AddressCheckOutcome Outcome, string? ProvinceName = null, string? WardName = null);

/// <summary>
/// The two-level list of administrative units (since 1/7/2025: provinces and wards, no district), read from the outside list and kept 24 hours per
/// list, sorted by name without diacritics. A failed read is never kept, so the next call asks again. <c>TryProvincesAsync</c> and <c>TryWardsAsync</c> give null while the list cannot be read.
/// </summary>
public sealed class AdministrativeUnitService(IAdministrativeUnitSource source, IMemoryCache cache)
{
    public static readonly TimeSpan CacheTime = TimeSpan.FromHours(24);

    private const string ProvincesKey = "administrative-units:provinces";

    public async Task<Result<IReadOnlyList<ProvinceUnit>>> ProvincesAsync(CancellationToken cancellationToken)
    {
        var provinces = await TryProvincesAsync(cancellationToken);
        return provinces is null ? Unavailable() : Result<IReadOnlyList<ProvinceUnit>>.Success(provinces);
    }

    /// <summary>The wards of a province; 404 for a code that is not a province.</summary>
    public async Task<Result<IReadOnlyList<WardUnit>>> WardsAsync(int provinceCode, CancellationToken cancellationToken)
    {
        var provinces = await TryProvincesAsync(cancellationToken);
        if (provinces is null)
        {
            return Unavailable();
        }

        if (provinces.All(p => p.Code != provinceCode))
        {
            return AppError.NotFound;
        }

        var wards = await TryWardsAsync(provinceCode, cancellationToken);
        return wards is null ? Unavailable() : Result<IReadOnlyList<WardUnit>>.Success(wards);
    }

    public async Task<IReadOnlyList<ProvinceUnit>?> TryProvincesAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(ProvincesKey, out IReadOnlyList<ProvinceUnit>? kept) && kept is not null)
        {
            return kept;
        }

        var read = await source.GetProvincesAsync(cancellationToken);
        if (read is not { Count: > 0 })
        {
            return null;
        }

        var provinces = read.OrderBy(p => AddressNames.Key(p.Name, AddressLevel.Province), StringComparer.Ordinal).ToList();
        cache.Set(ProvincesKey, (IReadOnlyList<ProvinceUnit>)provinces, CacheTime);
        return provinces;
    }

    /// <summary>The wards of a province already known to be in the list; null while they cannot be read.</summary>
    public async Task<IReadOnlyList<WardUnit>?> TryWardsAsync(int provinceCode, CancellationToken cancellationToken)
    {
        var key = $"administrative-units:wards:{provinceCode}";
        if (cache.TryGetValue(key, out IReadOnlyList<WardUnit>? kept) && kept is not null)
        {
            return kept;
        }

        var read = await source.GetWardsAsync(provinceCode, cancellationToken);
        if (read is null)
        {
            return null;
        }

        var wards = read.OrderBy(w => AddressNames.Key(w.Name, AddressLevel.Ward), StringComparer.Ordinal).ThenBy(w => w.Code).ToList();
        cache.Set(key, (IReadOnlyList<WardUnit>)wards, CacheTime);
        return wards;
    }

    /// <summary>
    /// Checks the codes of a saved address against the list: the province exists and the ward belongs to it. While the list cannot be read the check is
    /// skipped (<see cref="AddressCheckOutcome.NotChecked"/>) so an outage of the outside list does not stop a save.
    /// </summary>
    public async Task<AddressCheck> CheckAsync(int? provinceCode, int? wardCode, CancellationToken cancellationToken)
    {
        if (provinceCode is null)
        {
            return new AddressCheck(AddressCheckOutcome.Valid);
        }

        var provinces = await TryProvincesAsync(cancellationToken);
        if (provinces is null)
        {
            return new AddressCheck(AddressCheckOutcome.NotChecked);
        }

        if (provinces.FirstOrDefault(p => p.Code == provinceCode) is not { } province)
        {
            return new AddressCheck(AddressCheckOutcome.UnknownProvince);
        }

        if (wardCode is null)
        {
            return new AddressCheck(AddressCheckOutcome.Valid, province.Name);
        }

        var wards = await TryWardsAsync(province.Code, cancellationToken);
        if (wards is null)
        {
            return new AddressCheck(AddressCheckOutcome.NotChecked);
        }

        return wards.FirstOrDefault(w => w.Code == wardCode) is { } ward
            ? new AddressCheck(AddressCheckOutcome.Valid, province.Name, ward.Name)
            : new AddressCheck(AddressCheckOutcome.UnknownWard, province.Name);
    }

    /// <summary>
    /// The codes of the province and ward an old free-text entry names, matched by name (see <see cref="AddressNames"/>); a code is null when the name matches none
    /// or the list cannot be read. A ward is looked for only in the province matched.
    /// </summary>
    public async Task<AddressNameMatch> MatchNamesAsync(string? provinceName, string? wardName, CancellationToken cancellationToken)
    {
        if (await TryProvincesAsync(cancellationToken) is not { } provinces || AddressSplitter.MatchProvince(provinceName, provinces) is not { } province)
        {
            return new AddressNameMatch(null, null);
        }

        var wards = string.IsNullOrWhiteSpace(wardName) ? null : await TryWardsAsync(province.Code, cancellationToken);
        return new AddressNameMatch(province.Code, wards is null ? null : AddressSplitter.MatchWard(wardName, wards)?.Code);
    }

    private static AppError Unavailable() => new(ErrorKind.ServiceUnavailable, UserMessages.AdministrativeUnitsNotResponding);
}
