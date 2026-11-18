namespace OMTS.Application.Common.Interfaces.Addresses;

/// <summary>A province or centrally-run city of the two-level list in force since 1/7/2025.</summary>
/// <param name="DivisionType">As the list gives it, e.g. "tỉnh" or "thành phố trung ương".</param>
public sealed record ProvinceUnit(int Code, string Name, string DivisionType);

/// <summary>A ward, commune or special zone of a province.</summary>
public sealed record WardUnit(int Code, string Name, string DivisionType, int ProvinceCode);

/// <summary>
/// The list of administrative units (provinces and wards) as an outside service gives it. Failures are returned as null, never thrown: the caller
/// answers "service not responding" and never caches a failure. Only a code is sent out, no user data.
/// </summary>
public interface IAdministrativeUnitSource
{
    /// <summary>Every province; null when the service did not answer or its answer could not be read.</summary>
    Task<IReadOnlyList<ProvinceUnit>?> GetProvincesAsync(CancellationToken cancellationToken);

    /// <summary>The wards of a province; empty for a code the service does not know, null when it did not answer or its answer could not be read.</summary>
    Task<IReadOnlyList<WardUnit>?> GetWardsAsync(int provinceCode, CancellationToken cancellationToken);
}
