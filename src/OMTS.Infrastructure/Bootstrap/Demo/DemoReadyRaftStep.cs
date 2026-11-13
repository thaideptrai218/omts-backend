using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Farming.Rafts;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// Five empty rafts to start cycles on from the nursery pool (owner decision of 13/11/2026, v2): B1-11 to B1-15 in the first In Use demo zone, In Use, with 12, 16, 20, 24 and 18 lanes and NO cycle, so the Owner can
/// start a cycle from a "đã ươm xong" batch and seed it lane by lane (a cycle is started only on a raft with no Open cycle). Created through the raft service like the others: numeric geometry (a line 12 m long and a width
/// of one metre per lane plus two, so no lane is narrower than 1 m across), a map pin in a third row beside the two rows of <see cref="DemoRaftStep"/>, and the location of the zone. Matched by zone and
/// code; a raft that exists is left as it is, whatever happened to it since. Runs last, so no other demo step (water readings, material, harvest, cycles) takes these rafts into its data.
/// </summary>
internal sealed class DemoReadyRaftStep : IDemoSeedStep
{
    private const int FirstNumber = 11;
    private static readonly int[] LaneCounts = [12, 16, 20, 24, 18];

    public string Name => "empty rafts for nursery";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var zone = await context.Db.FarmingZones.AsNoTracking().SingleAsync(z => z.Code == DemoFarmingZoneStep.ZoneOne, cancellationToken);
        var service = context.Get<RaftService>();
        var created = 0;
        for (var index = 0; index < LaneCounts.Length; index++)
        {
            var code = DemoRaftStep.RaftCode(zone.Code, FirstNumber + index);
            if (await context.Db.Rafts.AnyAsync(r => r.ZoneId == zone.Id && r.Code == code, cancellationToken))
            {
                continue;
            }

            var lanes = LaneCounts[index];
            var request = new SaveRaftRequest(
                code, (zone.Latitude ?? 21m) + index * 0.00022m, (zone.Longitude ?? 107m) + 2 * 0.00025m, zone.Location, null, "Bè mới, chưa có chu kỳ, sẵn sàng bắt đầu chu kỳ từ đợt ươm", lanes,
                LengthM: 12m, WidthM: lanes + 2m);
            DemoSeedContext.Ensure(await service.CreateAsync(zone.Id, request, cancellationToken), $"raft {code}");
            created++;
        }

        return created;
    }
}
