using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>Two demo farming zones for development; idempotent by zone code. Never runs in Production.</summary>
internal static class DemoDataSeeder
{
    private static readonly (string Code, string Name, decimal Latitude, decimal Longitude, decimal Area)[] Zones =
    [
        ("DEMO-01", "Demo zone 01", 21.071200m, 107.432100m, 12.5m),
        ("DEMO-02", "Demo zone 02", 21.083400m, 107.451800m, 8.0m),
    ];

    public static async Task<IReadOnlyList<string>> SeedAsync(OmtsDbContext db, Guid cooperativeId, CancellationToken cancellationToken)
    {
        var codes = Zones.Select(z => z.Code).ToList();
        var existing = await db.FarmingZones.Where(z => codes.Contains(z.Code)).Select(z => z.Code).ToListAsync(cancellationToken);

        var created = new List<string>();
        foreach (var zone in Zones.Where(z => !existing.Contains(z.Code)))
        {
            db.FarmingZones.Add(new FarmingZone
            {
                CooperativeId = cooperativeId,
                Code = zone.Code,
                Name = zone.Name,
                Location = $"{zone.Name}, demo data",
                Latitude = zone.Latitude,
                Longitude = zone.Longitude,
                Area = zone.Area,
                WaterMeasurementPoint = "Central marker buoy",
                PhMin = 7.50m,
                PhMax = 8.50m,
                SalinityMin = 15.00m,
                SalinityMax = 35.00m,
            });
            created.Add(zone.Code);
        }

        return created;
    }
}
