using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// FARM-04: ten rafts in each In Use demo zone (codes B1-01…B1-10 and B2-01…B2-10), each with three to five lanes. The first
/// raft of a zone also has two photos and a Removed lane, the second one photo, and the third no map pin. The last two of
/// every zone are discontinued afterwards, as the Owner would, so lists show both statuses. Matched by zone and raft code;
/// a raft that exists is left as it is.
/// </summary>
internal sealed class DemoRaftStep : IDemoSeedStep
{
    private const int RaftsPerZone = 10;
    private const int DiscontinuedPerZone = 2;

    public string Name => "rafts";

    /// <summary>Code of the n-th raft (1-based) of a zone, e.g. "B1-03".</summary>
    public static string RaftCode(string zoneCode, int number) => $"B{zoneCode[^1]}-{number:00}";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var service = context.Get<RaftService>();
        var created = 0;
        foreach (var zoneCode in new[] { DemoFarmingZoneStep.ZoneOne, DemoFarmingZoneStep.ZoneTwo })
        {
            var zone = await context.Db.FarmingZones.SingleAsync(z => z.Code == zoneCode, cancellationToken);
            for (var number = 1; number <= RaftsPerZone; number++)
            {
                var code = RaftCode(zoneCode, number);
                var raft = await context.Db.Rafts.FirstOrDefaultAsync(r => r.ZoneId == zone.Id && r.Code == code, cancellationToken);
                if (raft is null)
                {
                    var saved = DemoSeedContext.Ensure(await service.CreateAsync(zone.Id, Request(zone.Latitude, zone.Longitude, code, number), cancellationToken), $"raft {code}");
                    await AddExtrasAsync(context, service, zone.Latitude, zone.Longitude, saved.Raft, number, cancellationToken);
                    raft = await context.Db.Rafts.FirstAsync(r => r.Id == saved.Raft.Id, cancellationToken);
                    created++;
                }

                if (number > RaftsPerZone - DiscontinuedPerZone && raft.StatusId == StatusIds.Usage.InUse)
                {
                    DemoSeedContext.Ensure(
                        await context.Get<RaftStatusService>().DiscontinueAsync(raft.Id, new ChangeStatusRequest("Bè cũ, đã tháo dỡ", RowVersionGuard.Encode(raft.RowVersion)), cancellationToken),
                        $"discontinuing raft {code}");
                }
            }
        }

        return created;
    }

    private static async Task AddExtrasAsync(
        DemoSeedContext context, RaftService service, decimal? latitude, decimal? longitude, RaftView raft, int number, CancellationToken cancellationToken)
    {
        var photos = context.Get<RaftPhotoService>();
        var current = raft;
        for (var photo = 1; photo <= 3 - number; photo++)
        {
            current = DemoSeedContext.Ensure(await photos.AddAsync(raft.Id, DemoFiles.RaftPhoto(photo), cancellationToken), $"photo {photo} of raft {raft.Code}").Raft;
        }

        if (number == 1)
        {
            var fewerLanes = Request(latitude, longitude, raft.Code, number) with { LaneCount = raft.LaneCount - 1, RowVersion = current.RowVersion };
            DemoSeedContext.Ensure(await service.UpdateAsync(raft.Id, fewerLanes, cancellationToken), $"removing a lane of raft {raft.Code}");
        }
    }

    private static SaveRaftRequest Request(decimal? zoneLatitude, decimal? zoneLongitude, string code, int number)
    {
        // Rafts sit in two rows next to the zone's centre marker, about 25 m apart; the third has no pin yet.
        var latitude = number == 3 ? (decimal?)null : (zoneLatitude ?? 21m) + ((number - 1) % 5) * 0.00022m;
        var longitude = number == 3 ? (decimal?)null : (zoneLongitude ?? 107m) + ((number - 1) / 5) * 0.00025m;
        return new SaveRaftRequest(
            code, latitude, longitude, "Vịnh Vân Đồn, Quảng Ninh", "10 m x 24 m", "Bè gỗ, phao nhựa HDPE, tình trạng tốt", 3 + number % 3);
    }
}
