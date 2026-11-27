using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>The seeded VAT rates for tests (the keys are the fixed ids of the seed), and an edit of one of them.</summary>
internal static class VatKit
{
    private sealed class SeededRate : VatRate
    {
        public SeededRate(Guid id) => Id = id;
    }

    private static VatRate Rate(Guid id, string name, decimal? percent, int sortOrder, bool isDefault = false) => new SeededRate(id)
    {
        Name = name, Percent = percent, IsExempt = percent is null, IsSystem = percent is null, IsDefault = isDefault, IsActive = true, SortOrder = sortOrder,
    };

    /// <summary>KCT (system, exempt), 0%, 5%, 8% and 10% (the default), as the migration seeds them.</summary>
    public static Dictionary<Guid, VatRate> Catalogue() => new[]
    {
        Rate(VatRateIds.Kct, "KCT", null, 0),
        Rate(VatRateIds.Zero, "0%", 0m, 1),
        Rate(VatRateIds.Five, "5%", 5m, 2),
        Rate(VatRateIds.Eight, "8%", 8m, 3),
        Rate(VatRateIds.Ten, "10%", 10m, 4, isDefault: true),
    }.ToDictionary(r => r.Id);

    /// <summary>What the Owner does in the settings: the rate keeps its id and gets another name and percent.</summary>
    public static void Edit(IReadOnlyDictionary<Guid, VatRate> catalogue, Guid id, string name, decimal? percent)
    {
        catalogue[id].Name = name;
        catalogue[id].Percent = percent;
    }
}
