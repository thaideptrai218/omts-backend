using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// FARM-02: three zones in Vân Đồn bay. DEMO-01 and DEMO-02 stay In Use; DEMO-03 is created and then
/// discontinued like a real zone (it has no rafts, so nothing blocks it). Matched by zone code.
/// </summary>
internal sealed class DemoFarmingZoneStep : IDemoSeedStep
{
    public const string ZoneOne = "DEMO-01";
    public const string ZoneTwo = "DEMO-02";
    public const string DiscontinuedZone = "DEMO-03";

    private static readonly (string Code, string Name, string Location, decimal Latitude, decimal Longitude, decimal Area, bool Discontinued)[] Zones =
    [
        (ZoneOne, "Vùng nuôi Ngọc Vừng", "Vịnh Ngọc Vừng, Huyện Vân Đồn, Quảng Ninh", 21.071200m, 107.432100m, 12.5m, false),
        (ZoneTwo, "Vùng nuôi Minh Châu", "Vịnh Minh Châu, Huyện Vân Đồn, Quảng Ninh", 21.083400m, 107.451800m, 8.0m, false),
        (DiscontinuedZone, "Vùng nuôi Quan Lạn (đã ngừng)", "Bãi Sơn Hào, Xã Quan Lạn, Huyện Vân Đồn, Quảng Ninh", 21.094700m, 107.478300m, 5.0m, true),
    ];

    public string Name => "zones";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<FarmingZoneService>();
        var created = 0;
        foreach (var zone in Zones)
        {
            var existing = await context.Db.FarmingZones.FirstOrDefaultAsync(z => z.Code == zone.Code, cancellationToken);
            if (existing is null)
            {
                var view = DemoSeedContext.Ensure(await service.CreateAsync(new SaveFarmingZoneRequest(
                    zone.Code, zone.Name, zone.Location, zone.Latitude, zone.Longitude, zone.Area, "Phao tiêu trung tâm vùng"), cancellationToken), $"zone {zone.Code}");
                (existing, created) = (await context.Db.FarmingZones.FirstAsync(z => z.Id == view.Id, cancellationToken), created + 1);
            }

            if (zone.Discontinued && existing.StatusId == StatusIds.Usage.InUse)
            {
                DemoSeedContext.Ensure(
                    await service.DiscontinueAsync(existing.Id, new ChangeStatusRequest("Vùng nuôi hết hạn giao khu vực biển", Convert.ToBase64String(existing.RowVersion)), cancellationToken),
                    $"discontinuing zone {zone.Code}");
            }
        }

        return created;
    }
}
