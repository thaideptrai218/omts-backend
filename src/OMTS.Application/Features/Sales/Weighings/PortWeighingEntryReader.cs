using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.Weighings.Dtos;
using OMTS.Application.Features.Sales.Weighings.Models;

namespace OMTS.Application.Features.Sales.Weighings;

/// <summary>The shape of the lots of a weighing: MSG-GEN-01 names every field to fix. What they name is checked against the database after.</summary>
internal static class PortWeighingEntryReader
{
    /// <summary>A limit on the size of the request body, far above a day's work at the port.</summary>
    public const int MaxCages = 5_000;

    public static (IReadOnlyList<PortWeighingLotEntries> Lots, List<string> Invalid) Read(IReadOnlyList<PortWeighingLotEntry?>? entries)
    {
        var invalid = new List<string>();
        var lots = new List<PortWeighingLotEntries>();
        if (entries is null || entries.Count == 0 || entries.Sum(e => e?.CageKgs?.Count ?? 0) > MaxCages)
        {
            invalid.Add("Lots");
            return (lots, invalid);
        }

        var seen = new HashSet<Guid>();
        for (var i = 0; i < entries.Count; i++)
        {
            var number = i + 1;
            var entry = entries[i];
            var before = invalid.Count;
            if (entry?.HarvestLotId is not { } lotId || !seen.Add(lotId))
            {
                invalid.Add($"Lot {number} harvest lot");
            }

            if (entry?.CageKgs is not { Count: > 0 } weighed || weighed.Any(kg => !InvoiceMoney.IsKg(kg)))
            {
                invalid.Add($"Lot {number} cage kg");
            }

            if (invalid.Count == before)
            {
                lots.Add(new PortWeighingLotEntries(entry!.HarvestLotId!.Value, entry.CageKgs!.Select(kg => kg!.Value).ToList()));
            }
        }

        return (lots, invalid);
    }
}
