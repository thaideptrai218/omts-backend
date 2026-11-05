using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// The orphan file clean-up on the shared <see cref="DailyJobHostedService"/> (PR 0.8), driven by a manual clock: 03:00
/// business time, no catch-up at start unless configured, nothing at all when disabled, one registration.
/// </summary>
public class OrphanFileCleanupScheduleTests
{
    private static readonly DateOnly Day = Today;

    private static DateTimeOffset Local(int hour, int days = 0) => new(Day.AddDays(days).ToDateTime(new TimeOnly(hour, 0)), BusinessClock.UtcOffset);

    [Fact]
    public async Task Runs_at_03_00_and_a_start_after_it_waits_for_the_next_day_by_default()
    {
        using var api = new AuthApiFactory();
        var orphan = await OrphanLicenseFileAsync(api);
        var clock = new ManualClock(Local(8));
        var hosted = Hosted(api, new OrphanFileCleanupOptions(), clock);

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await clock.WaitForTimerAsync();
            Assert.Equal(Local(3, days: 1), clock.NextDueAt);
            Assert.True(File.Exists(orphan));

            await clock.AdvanceToAsync(Local(2, days: 1));
            Assert.True(File.Exists(orphan));
            await clock.AdvanceToAsync(Local(3, days: 1));
            await WaitUntilAsync(() => !File.Exists(orphan));
            await WaitUntilAsync(() => clock.NextDueAt == Local(3, days: 2));
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task With_catch_up_on_a_start_after_03_00_runs_at_once()
    {
        using var api = new AuthApiFactory();
        var orphan = await OrphanLicenseFileAsync(api);
        var clock = new ManualClock(Local(8));
        var hosted = Hosted(api, new OrphanFileCleanupOptions { CatchUpOnStart = true }, clock);

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => !File.Exists(orphan));
            await clock.WaitForTimerAsync();
            Assert.Equal(Local(3, days: 1), clock.NextDueAt);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disabled_it_never_runs_or_waits()
    {
        using var api = new AuthApiFactory();
        var orphan = await OrphanLicenseFileAsync(api);
        var clock = new ManualClock(Local(8));
        var hosted = Hosted(api, new OrphanFileCleanupOptions { Enabled = false, CatchUpOnStart = true }, clock);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.ExecuteTask!;
        await hosted.StopAsync(CancellationToken.None);

        Assert.Null(clock.NextDueAt);
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public void Is_registered_once_with_its_defaults()
    {
        using var api = new AuthApiFactory();

        Assert.Single(api.Services.GetServices<IHostedService>(), h => h is OrphanFileCleanupHostedService);
        var defaults = new OrphanFileCleanupOptions();
        Assert.Equal((new TimeOnly(3, 0), false, true, TimeSpan.FromHours(24)), (defaults.RunAt, defaults.CatchUpOnStart, defaults.Enabled, defaults.GracePeriod));
    }

    private static OrphanFileCleanupHostedService Hosted(AuthApiFactory api, OrphanFileCleanupOptions options, TimeProvider clock) => new(
        new OrphanFileCleanupJob(api.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<OrphanFileCleanupJob>.Instance),
        Options.Create(options),
        clock,
        NullLogger<OrphanFileCleanupHostedService>.Instance);

    /// <summary>
    /// A license whose file the database points to (a folder with no referenced file is skipped as a safeguard) and an
    /// unreferenced license file written two days ago; returns the path of the latter.
    /// </summary>
    private static async Task<string> OrphanLicenseFileAsync(AuthApiFactory api)
    {
        var owner = await api.OwnerAsync();
        await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneId = (await owner.Client.CreateZoneAsync()).Id(), EffectiveDate = Today });
        var validated = FileValidator.Validate([new IncomingFile("file", Pdf.Length, new MemoryStream(Pdf))], FileRules.Documents);
        var stored = await api.Services.GetRequiredService<IFileStorage>().UploadAsync(validated.Files[0], FileCategories.ZoneLicenses);
        var path = Path.Combine(api.StorageRoot, stored.Key!);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Condition not reached within 15 seconds.");
            await Task.Delay(20);
        }
    }
}
