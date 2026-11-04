using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>What the GFR-04 tests set up and read. Each test has its own database: the job scans every zone.</summary>
public static class WaterOverdueTestKit
{
    public static DateTimeOffset LocalMorning(DateOnly date) => new(date.ToDateTime(new TimeOnly(8, 0)), BusinessClock.UtcOffset);

    /// <summary>An Open cycle started on <paramref name="start"/>. The tests name the column of the start here: <c>OpenedAt</c> until the merge with OMTS-220.</summary>
    public static CultivationCycle OpenCycle(Guid raftId, DateOnly start) =>
        new() { RaftId = raftId, OpenedAt = LocalMorning(start), StatusId = StatusIds.CultivationCycle.Open };

    public static CultivationCycle ClosedCycle(Guid raftId, DateOnly start)
    {
        var cycle = OpenCycle(raftId, start);
        cycle.StatusId = StatusIds.CultivationCycle.Closed;
        return cycle;
    }

    /// <summary>An Open cycle on the raft of the zone, or on <paramref name="raft"/>.</summary>
    public static Task OpenCycleAsync(this WaterZone z, DateOnly start, Raft? raft = null) =>
        z.Api.UseDbAsync(db => db.CultivationCycles.Add(OpenCycle((raft ?? z.Raft).Id, start)));

    /// <summary>A measurement in the database, for a time or a status no request could give.</summary>
    public static Task AddMeasurementAsync(this WaterZone z, DateOnly measured, int statusId = StatusIds.WaterMeasurement.Active) =>
        z.Api.UseDbAsync(db => db.WaterMeasurements.Add(new WaterMeasurement
        {
            ZoneId = z.Zone.Id,
            RaftId = z.Raft.Id,
            MeasuredAt = LocalMorning(measured),
            Ph = 7.5m,
            Salinity = 20m,
            Temperature = 25m,
            PhotoUrl = Guid.NewGuid().ToString("N"),
            ClientOperationId = Guid.NewGuid(),
            StatusId = statusId,
        }));

    /// <summary>One run of the job on <paramref name="today"/>; how many zones it reminded.</summary>
    public static async Task<int> RemindOnAsync(this WebApplicationFactory<Program> api, DateOnly today)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RunOnceAsync(today, CancellationToken.None);
    }

    public static Task<List<Notification>> RemindersOfAsync(this AuthApiFactory api, FarmingZone zone) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationTypes.WaterMeasurementOverdue && n.RelatedRecordId == zone.Id)
            .ToListAsync());

    /// <summary>The last date MSG-WATER-06 names, as dd/MM/yyyy.</summary>
    public static string Named(DateOnly date) => date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
}
