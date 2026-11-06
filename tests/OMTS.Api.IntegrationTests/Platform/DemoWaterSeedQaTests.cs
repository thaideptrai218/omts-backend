using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// QA of the water demo step on SQL Server: it goes through the services (alerts and their notifications
/// to the zone's recipients, audit rows by the Owner, thresholds used), the reminder as the README describes it,
/// Owner changes kept by a rerun, and an interrupted first run completed later on the same days.
/// </summary>
public class DemoWaterSeedQaTests
{
    private const string DemoPassword = "Demo@1234";
    private static readonly string[] InUseZones = ["DEMO-01", "DEMO-02"];

    [SqlServerFact]
    public async Task Demo_water_data_comes_from_the_services_with_alerts_notifications_and_audit_by_the_owner()
    {
        await using var host = new DemoWaterHost();
        var run = await host.RunAsync();
        Assert.Equal(DeploymentBootstrapper.Success, run.ExitCode);
        Assert.Contains("Demo water measurements: 60 created.", run.Messages);

        using var scope = host.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var recipients = scope.ServiceProvider.GetRequiredService<IZoneRecipientQuery>();
        var ownerId = await db.Accounts.Where(a => a.Username == "owner").Select(a => a.Id).SingleAsync();
        var water = await db.WaterMeasurements.AsNoTracking().Include(m => m.Zone).ToListAsync();

        // Three readings stay outside the thresholds; the mistyped salinity (2.6 ‰) alerted too, before its fix, so four sets of alerts.
        var alerted = water.Where(m => m.AlertedAt is not null).ToList();
        Assert.Equal(3, alerted.Count);
        Assert.All(alerted, m => Assert.True(m.ThresholdExceeded && m.ViolatedValues is { Length: > 0 }, m.Zone.Code));
        foreach (var measurement in alerted)
        {
            var expected = await recipients.GetRecipientsAsync(measurement.ZoneId, PermissionFunctions.WaterMeasurements, PermissionAction.View, default);
            var notified = await db.Notifications.AsNoTracking()
                .Where(n => n.Type == NotificationTypes.WaterAlert && n.RelatedRecordId == measurement.Id)
                .Select(n => n.RecipientAccountId).ToListAsync();
            // The demo records as the Owner, who is not told of their own action; the zone's staff are.
            Assert.DoesNotContain(ownerId, notified);
            Assert.NotEmpty(notified);
            Assert.Equal(expected.Where(id => id != ownerId).Order(), notified.Order());
        }

        // Only alerts: no reminder, no other water notification.
        Assert.Equal(
            4,
            await db.Notifications.Where(n => n.Type == NotificationTypes.WaterAlert).Select(n => n.RelatedRecordId).Distinct().CountAsync());
        Assert.False(await db.Notifications.AnyAsync(n => n.Type == NotificationTypes.WaterMeasurementOverdue));

        // The correction changes the same record: salinity 26.0, a reason in the audit log, its alert no longer current.
        var correctedId = await db.AuditEvents.Where(e => e.Action == WaterMeasurementAudit.Corrected).Select(e => e.RecordId).SingleAsync();
        var correction = Assert.Single(water, m => m.Id == correctedId);
        Assert.Equal((StatusIds.WaterMeasurement.Active, 26.0m), (correction.StatusId, correction.Salinity));
        Assert.Null(correction.AlertedAt);
        Assert.Equal("DEMO-01", correction.Zone.Code);
        var voided = Assert.Single(water, m => m.StatusId == StatusIds.WaterMeasurement.Voided);
        Assert.Equal("DEMO-02", voided.Zone.Code);

        // Every reading used the demo thresholds and was measured in the early morning, Vietnam time.
        Assert.All(water, m => Assert.Equal((7.5m, 8.5m, 18m, 32m, 15m, 32m), (m.PhMinUsed!.Value, m.PhMaxUsed!.Value, m.SalinityMinUsed!.Value, m.SalinityMaxUsed!.Value, m.TemperatureMinUsed!.Value, m.TemperatureMaxUsed!.Value)));
        Assert.All(water, m => Assert.InRange(m.MeasuredAt.ToOffset(BusinessClock.UtcOffset).TimeOfDay, TimeSpan.FromHours(6.5), TimeSpan.FromHours(7)));
        Assert.All(water, m => Assert.Equal(ownerId, m.MeasuredByAccountId));

        // Audit rows of the normal rules, all by the Owner.
        var audit = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action.StartsWith("WaterMeasurement.") || e.Action == WaterMeasurementAudit.ThresholdUpdated)
            .ToListAsync();
        Assert.Equal(60, audit.Count(e => e.Action == WaterMeasurementAudit.Created));
        var corrected = Assert.Single(audit, e => e.Action == WaterMeasurementAudit.Corrected && e.RecordId == correction.Id);
        Assert.False(string.IsNullOrWhiteSpace(corrected.Reason));
        Assert.Contains("\"Salinity\":2.6", corrected.Before);
        Assert.Equal(1, audit.Count(e => e.Action == WaterMeasurementAudit.Voided && e.RecordId == voided.Id));
        Assert.Equal(2, audit.Count(e => e.Action == WaterMeasurementAudit.ThresholdUpdated));
        Assert.All(audit, e => Assert.Equal(ownerId, e.ActorAccountId));
    }

    // The demo now seeds Open cycles. Remove them in this disposable database to exercise the no-cycle case, then
    // add one back: the zone is on time until a month after its last reading, then it is reminded.
    [SqlServerFact]
    public async Task Demo_sends_no_reminder_without_an_open_cycle_and_a_zone_with_one_is_reminded_a_month_after_its_last_reading()
    {
        await using var host = new DemoWaterHost();
        await host.RunAsync();
        var (zoneId, raftId, lastReading) = await host.QueryAsync(async db =>
        {
            var zone = await db.FarmingZones.SingleAsync(z => z.Code == "DEMO-01");
            var raft = await db.Rafts.Where(r => r.ZoneId == zone.Id).OrderBy(r => r.Code).Select(r => r.Id).FirstAsync();
            var last = await db.WaterMeasurements.AsNoTracking()
                .Where(m => m.ZoneId == zone.Id && m.StatusId == StatusIds.WaterMeasurement.Active)
                .MaxAsync(m => m.MeasuredAt);
            return (zone.Id, raft, last);
        });
        var lastDayOnTime = BusinessClock.ToBusinessDate(lastReading).AddMonths(1);

        await host.QueryAsync(async db =>
        {
            var seededCycles = await db.CultivationCycles.ToListAsync();
            Assert.NotEmpty(seededCycles);
            db.CultivationCycles.RemoveRange(seededCycles);
            return await db.SaveChangesAsync();
        });
        Assert.False(await host.QueryAsync(db => db.CultivationCycles.AnyAsync()));
        Assert.Equal(0, await host.App.RemindOnAsync(lastDayOnTime.AddMonths(6)));

        await host.QueryAsync(async db =>
        {
            db.CultivationCycles.Add(WaterOverdueTestKit.OpenCycle(raftId, lastDayOnTime.AddMonths(-3)));
            return await db.SaveChangesAsync();
        });

        Assert.Equal(0, await host.App.RemindOnAsync(lastDayOnTime));
        Assert.Equal(1, await host.App.RemindOnAsync(lastDayOnTime.AddDays(1)));
        Assert.Equal(0, await host.App.RemindOnAsync(lastDayOnTime.AddDays(2)));

        var reminded = await host.QueryAsync(db => db.Notifications.Where(n => n.Type == NotificationTypes.WaterMeasurementOverdue)
            .Select(n => n.RelatedRecordId).Distinct().ToListAsync());
        Assert.Equal([zoneId], reminded.Select(id => id!.Value));
    }

    // Real use after the seed: the Owner changes DEMO-01's thresholds and voids one of its readings. A rerun keeps both.
    [SqlServerFact]
    public async Task Demo_rerun_keeps_owner_thresholds_and_an_owner_void_and_records_nothing_again()
    {
        await using var host = new DemoWaterHost();
        await host.RunAsync();
        var owner = await host.OwnerAsync();
        var (zoneId, readingId) = await host.QueryAsync(async db =>
        {
            var zone = await db.FarmingZones.SingleAsync(z => z.Code == "DEMO-01");
            var reading = await db.WaterMeasurements
                .Where(m => m.ZoneId == zone.Id && m.StatusId == StatusIds.WaterMeasurement.Active)
                .OrderByDescending(m => m.MeasuredAt).FirstAsync();
            return (zone.Id, reading.Id);
        });

        var thresholds = await owner.PutAsJsonAsync($"/api/zones/{zoneId}/water-thresholds", new { phMin = 7.0m, phMax = 9.0m, salinityMin = 15m, salinityMax = 35m, temperatureMin = 10m, temperatureMax = 35m });
        Assert.True(thresholds.StatusCode == HttpStatusCode.OK, await thresholds.Content.ReadAsStringAsync());
        var voided = await owner.PostAsJsonAsync($"/api/water-measurements/{readingId}/void", new { reason = "Ghi nhầm" });
        Assert.True(voided.StatusCode == HttpStatusCode.OK, await voided.Content.ReadAsStringAsync());
        var before = await host.QueryAsync(db => db.WaterMeasurements.AsNoTracking().OrderBy(m => m.Id).Select(m => new { m.Id, m.StatusId, m.MeasuredAt }).ToListAsync());

        var rerun = await host.RunAsync();

        Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
        Assert.Contains("Demo water measurements: already there.", rerun.Messages);
        var zone = await host.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(z => z.Id == zoneId));
        Assert.Equal((7.0m, 9.0m, 15m, 35m), (zone.PhMin!.Value, zone.PhMax!.Value, zone.SalinityMin!.Value, zone.SalinityMax!.Value));
        var after = await host.QueryAsync(db => db.WaterMeasurements.AsNoTracking().OrderBy(m => m.Id).Select(m => new { m.Id, m.StatusId, m.MeasuredAt }).ToListAsync());
        Assert.Equal(before, after);
    }

    // The first run stops after a few readings of DEMO-01 (crash, container stopped). Three days later the rerun must
    // complete the plan "on the same days" (step summary): 30 days before the first seed day for both zones.
    // BUG (Low): DEMO-02 had no stored reading yet, so its first seed day is the rerun day and its 30 days are moved.
    [SqlServerFact]
    public async Task Interrupted_first_run_is_completed_later_on_the_days_of_the_first_seed_for_every_zone()
    {
        var clock = new ShiftedClock();
        var crash = new CrashAfterWaterSaves(9);
        await using var host = new DemoWaterHost(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new CrashingUnitOfWork(provider.GetRequiredService<OmtsDbContext>(), crash));
        });
        var firstDay = clock.Today();

        await Assert.ThrowsAsync<InvalidOperationException>(host.RunAsync);
        Assert.Equal(9, await host.QueryAsync(db => db.WaterMeasurements.CountAsync()));

        crash.Disarm();
        clock.Shift = TimeSpan.FromDays(3);
        var rerun = await host.RunAsync();

        Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
        var water = await host.QueryAsync(db => db.WaterMeasurements.AsNoTracking().Include(m => m.Zone).ToListAsync());
        Assert.Equal(60, water.Count);
        Assert.DoesNotContain(water, m => m.Salinity == 2.6m);
        Assert.Single(water, m => m.StatusId == StatusIds.WaterMeasurement.Voided);
        var expected = Enumerable.Range(1, 30).Select(d => firstDay.AddDays(-d)).Order().ToList();
        var moved = InUseZones
            .Select(code => (Code: code, Days: water.Where(m => m.Zone.Code == code)
                .Select(m => BusinessClock.ToBusinessDate(m.MeasuredAt)).Order().ToList()))
            .Where(zone => !zone.Days.SequenceEqual(expected))
            .Select(zone => $"{zone.Code}: {zone.Days[0]:yyyy-MM-dd}..{zone.Days[^1]:yyyy-MM-dd}")
            .ToList();
        Assert.True(moved.Count == 0, $"Expected {expected[0]:yyyy-MM-dd}..{expected[^1]:yyyy-MM-dd} for every zone; moved: {string.Join(", ", moved)}");
    }

    /// <summary>The system clock moved forward by <see cref="Shift"/>, for a run "on a later day".</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }

    /// <summary>Fails the save after <c>allowed</c> saves that add a water measurement, as a crash in the middle of the step would.</summary>
    private sealed class CrashAfterWaterSaves(int allowed)
    {
        private int _remaining = allowed;
        private bool _armed = true;

        public void Disarm() => _armed = false;

        public bool ShouldCrash() => _armed && Interlocked.Decrement(ref _remaining) < 0;
    }

    /// <summary>The production unit of work (SaveChanges with the same exception mapping) plus the armed crash.</summary>
    private sealed class CrashingUnitOfWork(OmtsDbContext db, CrashAfterWaterSaves crash) : IUnitOfWork
    {
        private readonly UnitOfWork _inner = new(db, new OMTS.Infrastructure.Messaging.PushOutbox());

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var addsWater = db.ChangeTracker.Entries<WaterMeasurement>().Any(e => e.State == EntityState.Added);
            if (addsWater && crash.ShouldCrash())
            {
                throw new InvalidOperationException("Simulated crash in the middle of the demo seed.");
            }

            return _inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => _inner.ReloadAsync(entity, cancellationToken);

        public void DiscardChanges() => _inner.DiscardChanges();

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => _inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => _inner.MarkChanged(entity);
    }

    /// <summary>A fresh SQL Server database with the bootstrap secrets of a demo deployment; dropped with its files.</summary>
    private sealed class DemoWaterHost : IAsyncDisposable
    {
        private readonly SqlServerTestDatabase _database = SqlServerTestDatabase.Create();
        private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        private readonly SqlServerApiFactory _api;

        public DemoWaterHost(Action<IServiceCollection>? configureServices = null)
        {
            _api = new SqlServerApiFactory(_database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã Nuôi hàu Vân Đồn",
                [BootstrapSettings.OwnerDisplayNameKey] = "Nguyễn Văn Hải",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                // Generated per host, so no password-like literal is committed (secret scanners); the demo
                // step replaces it with the demo password anyway.
                [BootstrapSettings.OwnerTemporaryPasswordKey] = $"Tp{Guid.NewGuid():N}"[..12] + "7",
                ["Storage:Local:RootPath"] = _storageRoot,
            });
            App = configureServices is null ? _api : _api.WithWebHostBuilder(b => b.ConfigureTestServices(configureServices));
        }

        public WebApplicationFactory<Program> App { get; }

        public async Task<BootstrapOutcome> RunAsync()
        {
            using var scope = App.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
        }

        public async Task<T> QueryAsync<T>(Func<OmtsDbContext, Task<T>> query)
        {
            using var scope = App.Services.CreateScope();
            return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
        }

        public async Task<HttpClient> OwnerAsync()
        {
            var client = App.CreateClient();
            var login = await client.PostAsJsonAsync("/api/auth/login", new { identifier = "owner", password = DemoPassword });
            Assert.True(login.StatusCode == HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
            var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await _api.DisposeAsync();
            await _database.DisposeAsync();
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
    }
}
