using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>The checks and refusal texts of the VAT rate list.</summary>
public static class VatRateRules
{
    public const int MaxNameLength = 50;
    public const int MaxRates = 50;

    /// <summary>The <c>reasonCode</c> of the MSG-GEN-09 refusals.</summary>
    public const string SystemRate = "VAT_RATE_SYSTEM";
    public const string ExemptNotDefault = "VAT_RATE_EXEMPT_NOT_DEFAULT";
    public const string InactiveNotDefault = "VAT_RATE_INACTIVE_NOT_DEFAULT";
    public const string DefaultNotDeactivated = "VAT_RATE_DEFAULT_NOT_DEACTIVATED";

    public static string SystemRateText(string name) => $"{name} là mức thuế của hệ thống, không thể sửa hoặc ngừng sử dụng.";

    public static string ExemptNotDefaultText(string name) => $"{name} (không chịu thuế) không thể là mức thuế mặc định của vật tư.";

    public static string InactiveNotDefaultText(string name) => $"Mức thuế {name} đang ngừng sử dụng. Hãy dùng lại mức thuế này trước khi đặt làm mặc định.";

    public static string DefaultNotDeactivatedText(string name) => $"{name} đang là mức thuế mặc định. Hãy chọn mức mặc định khác trước khi ngừng sử dụng.";

    /// <summary>The rate a new supply item starts with: the active default; failing that the first active rate with a percent, and KCT as the last resort.</summary>
    public static VatRate DefaultOf(IReadOnlyDictionary<Guid, VatRate> catalogue) =>
        catalogue.Values.Where(r => r.IsActive && r.IsDefault && !r.IsExempt).OrderBy(r => r.SortOrder).FirstOrDefault()
        ?? catalogue.Values.Where(r => r.IsActive && !r.IsExempt).OrderBy(r => r.SortOrder).FirstOrDefault()
        ?? catalogue[VatRateIds.Kct];

    /// <summary>The fields of a save that are not valid: a name of 1 to 50 characters and a percent from 0 to 100 with at most 2 decimals.</summary>
    public static List<string> InvalidFields(string? name, decimal? percent)
    {
        var invalid = new List<string>();
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            invalid.Add("Name");
        }

        if (percent is not { } value || value < 0m || value > 100m || decimal.Round(value, 2) != value)
        {
            invalid.Add("Percent");
        }

        return invalid;
    }

    /// <summary>
    /// True when the replacement named for a rate to deactivate is not acceptable: it is required while items have the rate, and whenever it is named it must be a rate other than the one
    /// deactivated and still active.
    /// </summary>
    public static bool ReplacementProblem(VatRate rate, int itemCount, Guid? replacementId, VatRate? replacement)
    {
        if (replacementId is null)
        {
            return itemCount > 0;
        }

        return replacement is null || replacement.Id == rate.Id || !replacement.IsActive;
    }
}
