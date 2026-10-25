using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Notifications;
using OMTS.Infrastructure.Jobs;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// OMTS-214: the overdue water measurement job catches up once when the host starts after its run time
/// (<c>Jobs:WaterMeasurementOverdue</c>, like the zone license reminder); the once-per-period marker keeps a second
/// start on the same day from sending anything.
/// </summary>
public class WaterOverdueCatchUpTests
{
    private const string RunLine = "Overdue water measurement reminder sent for";
    private static readonly DateOnly Today = TimeProvider.System.Today();

    [Fact]
    public async Task Start_after_the_run_time_reminds_an_overdue_zone_once_and_a_second_start_the_same_day_sends_nothing()
    {
        await using var api = new WaterApiFactory();
        var z = await api.ZoneAsync();
        var (_, staff) = await api.StaffAsync(z.Zone);
        await z.RecordedAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        var afterRunTime = new FixedClock(new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), BusinessClock.UtcOffset));

        await StartAndWaitForRunsAsync(api, afterRunTime, runs: 1);
        var first = await RecipientsAsync(api, z);
        await StartAndWaitForRunsAsync(api, afterRunTime, runs: 2);

        Assert.Equal(new[] { z.OwnerAccount.Id, staff.Id }.Order(), first.Order());
        Assert.Equal(first, await RecipientsAsync(api, z));
        Assert.Contains(api.Logs.Lines, l => l.Contains($"{RunLine} 1 zones"));
        Assert.Contains(api.Logs.Lines, l => l.Contains($"{RunLine} 0 zones"));
    }

    [Theory]
    [InlineData("2026-10-22T22:59:00Z", false)]
    [InlineData("2026-10-22T23:00:00Z", true)]
    [InlineData("2026-10-23T09:00:00Z", true)]
    [InlineData("2026-10-22T17:00:00Z", false)]
    public void Default_run_time_is_06_00_business_time(string utc, bool catchUp)
    {
        var defaults = new WaterMeasurementOverdueOptions();

        Assert.True(defaults.CatchUpOnStart);
        Assert.Equal(catchUp, WaterMeasurementOverdueJob.IsPastRunTime(DateTimeOffset.Parse(utc), defaults.RunAt));
        // 05:59 local waits one minute; 06:00 waits a full day.
        Assert.Equal(TimeSpan.FromMinutes(1), WaterMeasurementOverdueJob.DelayUntilNextRun(DateTimeOffset.Parse("2026-10-22T22:59:00Z"), defaults.RunAt));
        Assert.Equal(TimeSpan.FromDays(1), WaterMeasurementOverdueJob.DelayUntilNextRun(DateTimeOffset.Parse("2026-10-22T23:00:00Z"), defaults.RunAt));
    }

    [Fact]
    public async Task Options_are_bound_from_the_jobs_section()
    {
        await using var api = new WaterApiFactory(new Dictionary<string, string> { ["Jobs:WaterMeasurementOverdue:RunAt"] = "05:30" });

        var options = api.Services.GetRequiredService<IOptions<WaterMeasurementOverdueOptions>>().Value;

        Assert.Equal(new TimeOnly(5, 30), options.RunAt);
        // The test hosts switch the catch-up off: tests run the jobs themselves.
        Assert.False(options.CatchUpOnStart);
    }

    /// <summary>Starts a job with catch-up on, waits until it has logged <paramref name="runs"/> runs in total, then stops it.</summary>
    private static async Task StartAndWaitForRunsAsync(WaterApiFactory api, TimeProvider clock, int runs)
    {
        var job = new WaterMeasurementOverdueJob(
            api.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WaterMeasurementOverdueOptions { CatchUpOnStart = true }),
            clock,
            api.Services.GetRequiredService<ILogger<WaterMeasurementOverdueJob>>());
        await job.StartAsync(CancellationToken.None);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (api.Logs.Lines.Count(l => l.Contains(RunLine)) < runs)
            {
                Assert.True(DateTime.UtcNow < until, "The catch-up run did not happen within 30 seconds.");
                await Task.Delay(100);
            }
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
    }

    private static Task<List<Guid>> RecipientsAsync(WaterApiFactory api, WaterZone z) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationTypes.WaterMeasurementOverdue && n.RelatedRecordCode == z.Zone.Code)
            .Select(n => n.RecipientAccountId)
            .OrderBy(id => id)
            .ToListAsync());
}
