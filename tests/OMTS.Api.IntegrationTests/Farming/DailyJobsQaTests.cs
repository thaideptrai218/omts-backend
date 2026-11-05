using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// QA of OMTS-217 PR 0.8 on the real daily jobs (the shared base is covered with a counting job in
/// <see cref="WaterOverdueJobScheduleQaTests"/>): stopping while they wait, a stop in the middle of a reminder run, and the
/// new clean-up setting read from the configuration.
/// </summary>
public class DailyJobsQaTests
{
    private static DateTimeOffset Local(int hour) => new(Today.ToDateTime(new TimeOnly(hour, 0)), BusinessClock.UtcOffset);

    [Fact]
    public async Task The_orphan_clean_up_stops_at_once_while_it_waits()
    {
        using var api = new AuthApiFactory();
        var clock = new ManualClock(Local(8));
        var options = new OrphanFileCleanupOptions();

        await AssertStopsAtOnceAsync(clock, new OrphanFileCleanupHostedService(
            new OrphanFileCleanupJob(api.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<OrphanFileCleanupJob>.Instance),
            Options.Create(options), clock, NullLogger<OrphanFileCleanupHostedService>.Instance));
    }

    [Fact]
    public async Task The_expiry_reminder_stops_at_once_while_it_waits()
    {
        using var api = new AuthApiFactory();
        var clock = new ManualClock(Local(6));

        await AssertStopsAtOnceAsync(clock, new ZoneLicenseReminderHostedService(
            api.Services.GetRequiredService<ZoneLicenseReminderJob>(), Options.Create(new ZoneLicenseReminderOptions()), clock, NullLogger<ZoneLicenseReminderHostedService>.Instance));
    }

    // A run stopped between two records keeps what was sent and saves nothing half-way; the next run sends the rest.
    [Fact]
    public async Task A_cancelled_reminder_run_ends_with_cancellation_and_sends_nothing_new()
    {
        using var api = new AuthApiFactory();
        var owner = await api.OwnerAsync();
        var license = await owner.Client.CreateLicenseAsync(new LicenseFields
        {
            ZoneId = (await owner.Client.CreateZoneAsync()).Id(), EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(3),
        });
        var job = api.Services.GetRequiredService<ZoneLicenseReminderJob>();
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.RunOnceAsync(Today, stopping.Token));

        Assert.Empty(await api.QueryAsync(db => db.Notifications.ToListAsync()));
        Assert.Null((await api.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == license.Id()))).ExpiringReminderSentAt);
        Assert.Equal(1, await job.RunOnceAsync(Today));
    }

    // The setting documented in the client change list reaches the job.
    [Fact]
    public void Catch_up_of_the_clean_up_is_read_from_its_settings_section()
    {
        using var api = new CatchUpSettingFactory();

        var options = api.Services.GetRequiredService<IOptions<OrphanFileCleanupOptions>>().Value;

        Assert.True(options.CatchUpOnStart);
        Assert.Equal(new TimeOnly(3, 0), options.RunAt);
    }

    private static async Task AssertStopsAtOnceAsync(ManualClock clock, BackgroundService hosted)
    {
        await hosted.StartAsync(CancellationToken.None);
        await clock.WaitForTimerAsync();

        var watch = Stopwatch.StartNew();
        await hosted.StopAsync(CancellationToken.None);
        watch.Stop();

        Assert.True(hosted.ExecuteTask!.IsCompletedSuccessfully, $"The job ended as {hosted.ExecuteTask.Status}.");
        // The job waits for a timer hours away, so five minutes prove it does not wait for it; a busy CI runner has taken over a minute.
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromMinutes(5));
    }

    private sealed class CatchUpSettingFactory : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting($"{OrphanFileCleanupOptions.SectionName}:CatchUpOnStart", "true");
        }
    }
}
