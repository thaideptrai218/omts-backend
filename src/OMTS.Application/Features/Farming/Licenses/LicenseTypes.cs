
namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>Zone license types (FARM-03) with their display names.</summary>
public static class LicenseTypes
{
    public const string SeaAreaAllocation = "SEA_AREA_ALLOCATION";
    public const string AquacultureLicense = "AQUACULTURE_LICENSE";
    public const string FoodSafetyCommitment = "FOOD_SAFETY_COMMITMENT";
    public const string Other = "OTHER";

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [SeaAreaAllocation] = "Sea area allocation decision",
        [AquacultureLicense] = "Marine aquaculture license",
        [FoodSafetyCommitment] = "Food safety commitment",
        [Other] = "Other document",
    };

    /// <summary>A zone without a Valid or Expiring soon document of these types shows a Missing row.</summary>
    public static readonly IReadOnlyList<string> Mandatory = [SeaAreaAllocation, AquacultureLicense];
}
