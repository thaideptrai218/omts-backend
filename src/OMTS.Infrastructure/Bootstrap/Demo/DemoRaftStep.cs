using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// FARM-04: ten rafts in each In Use demo zone (codes B1-01…B1-10 and B2-01…B2-10), each with three to
/// five lanes and a layout drawing. The last two of every zone are discontinued afterwards, as the Owner
/// would, so lists show both statuses. Matched by zone and raft code.
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
                    var view = DemoSeedContext.Ensure(await service.CreateAsync(zone.Id, Request(zone.Latitude, zone.Longitude, code, number), cancellationToken), $"raft {code}");
                    raft = await context.Db.Rafts.FirstAsync(r => r.Id == view.Id, cancellationToken);
                    created++;
                }

                if (number > RaftsPerZone - DiscontinuedPerZone && raft.StatusId == StatusIds.Usage.InUse)
                {
                    DemoSeedContext.Ensure(
                        await service.DiscontinueAsync(raft.Id, new ChangeStatusRequest("Bè cũ, đã tháo dỡ", Convert.ToBase64String(raft.RowVersion)), cancellationToken),
                        $"discontinuing raft {code}");
                }
            }
        }

        return created;
    }

    private static SaveRaftRequest Request(decimal? zoneLatitude, decimal? zoneLongitude, string code, int number)
    {
        // Rafts sit in two rows next to the zone's centre marker, about 25 m apart.
        var latitude = (zoneLatitude ?? 21m) + ((number - 1) % 5) * 0.00022m;
        var longitude = (zoneLongitude ?? 107m) + ((number - 1) / 5) * 0.00025m;
        var laneCount = 3 + number % 3;
        var lanes = Enumerable.Range(1, laneCount)
            .Select(lane => new LaneInput(lane, 20m + lane * 2m, "Đầu A", "Đầu B"))
            .ToList();
        var drawing = JsonSerializer.SerializeToElement(new
        {
            width = 10,
            height = 24,
            lanes = lanes.Select(l => new { number = l.LaneNumber, x = (l.LaneNumber - 1) * 2, y = 0, length = l.Length }),
        });
        return new SaveRaftRequest(
            code, latitude, longitude, "Vịnh Vân Đồn, Quảng Ninh", "10 m x 24 m", "Bè gỗ, phao nhựa HDPE, tình trạng tốt",
            lanes, drawing);
    }
}
