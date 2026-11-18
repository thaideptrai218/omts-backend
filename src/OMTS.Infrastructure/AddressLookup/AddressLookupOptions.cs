namespace OMTS.Infrastructure.AddressLookup;

/// <summary>
/// "AddressLookup" section: the two public outside services of the customer and supplier forms. Neither needs a key and neither is sent anything but a
/// province code or a tax code, so there is no secret here.
/// </summary>
public sealed class AddressLookupOptions
{
    public const string SectionName = "AddressLookup";

    /// <summary>The list of provinces and wards in force since 1/7/2025 (two levels, no district).</summary>
    public string AdministrativeUnitsBaseUrl { get; set; } = "https://provinces.open-api.vn/api/v2/";

    /// <summary>The business registry of the tax authority, looked up by tax code.</summary>
    public string TaxRegistryBaseUrl { get; set; } = "https://api.vietqr.io/v2/";

    /// <summary>After this a call is abandoned and the user is told the service is not responding.</summary>
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>An answer larger than this is refused: the biggest province list holds a few hundred small entries.</summary>
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
}
