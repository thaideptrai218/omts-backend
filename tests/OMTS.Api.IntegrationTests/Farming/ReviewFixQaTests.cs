using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>QA-1 checks of the OMTS-210 review fixes: drawing comparison, lanes in use, reminder, clock, paging.</summary>
public class ReviewFixQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Stored drawing; the variants below are the same JSON value written differently.
    private const string Stored = "{\"label\":\"Bè ố\",\"shapes\":[{\"kind\":\"rect\",\"x\":1,\"y\":2},{\"kind\":\"rect\",\"x\":3,\"y\":4}]}";

    // Other key order, whitespace, \u escapes for the Vietnamese letters, and \u003c for a tag-like character.
    private const string SameValueVariant =
        "{ \"shapes\" : [ { \"y\" : 2, \"x\" : 1, \"kind\" : \"rect\" }, {\"y\":4,\"x\":3,\"kind\":\"rect\"} ],\n  \"label\" : \"B\\u00e8 \\u1ed1\" }";

    /// <summary>Same drawing value in other key order, whitespace and escapes: no drawing change, no new layout version.</summary>
    [Fact]
    public async Task Same_drawing_value_written_differently_is_not_a_drawing_change()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var code = NewCode();
        var created = await (await owner.PostAsync($"/api/zones/{zone.Id()}/rafts", RawRaft(NewRaftBody(code), Stored))).ReadAsync(HttpStatusCode.Created);
        var id = created.Id();

        var same = await (await owner.PutAsync($"/api/rafts/{id}", RawRaft(NewRaftBody(code) with { RowVersion = created.Text("rowVersion") }, SameValueVariant)))
            .ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Bè ố", same.GetProperty("layoutDrawing").GetProperty("label").GetString());
        Assert.Equal(0, await DrawingChangesAsync(id));
        Assert.Equal(1, await LayoutVersionsAsync(id));
    }

    /// <summary>Other escapes of HTML-sensitive and other ASCII characters are the same value too.</summary>
    [Fact]
    public async Task Same_drawing_with_html_characters_written_as_escapes_is_not_a_drawing_change()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var code = NewCode();
        var created = await (await owner.PostAsync($"/api/zones/{zone.Id()}/rafts",
            RawRaft(NewRaftBody(code), "{\"note\":\"a < b & 'c' + d > e\"}"))).ReadAsync(HttpStatusCode.Created);

        await (await owner.PutAsync($"/api/rafts/{created.Id()}", RawRaft(NewRaftBody(code) with { RowVersion = created.Text("rowVersion") },
            "{\"note\":\"a \\u003C b \\u0026 \\u0027c\\u0027 \\u002B d \\u003E e\"}"))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(0, await DrawingChangesAsync(created.Id()));
    }

    /// <summary>A notes-only edit with the variant drawing keeps the drawing and records only the notes change.</summary>
    [Fact]
    public async Task Notes_edit_with_variant_drawing_records_no_drawing_change()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var code = NewCode();
        var created = await (await owner.PostAsync($"/api/zones/{zone.Id()}/rafts", RawRaft(NewRaftBody(code), Stored))).ReadAsync(HttpStatusCode.Created);
        var id = created.Id();

        await (await owner.PutAsync($"/api/rafts/{id}", RawRaft(NewRaftBody(code) with
        {
            ConditionNotes = "Dây neo mới",
            RowVersion = created.Text("rowVersion"),
        }, SameValueVariant))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(0, await DrawingChangesAsync(id));
        Assert.Equal(1, await UpdatedCountAsync(id));
        Assert.Equal(1, await LayoutVersionsAsync(id));
    }

    /// <summary>Real value changes inside a drawing (text, number, array order) each record one drawing change and one layout version.</summary>
    [Fact]
    public async Task Real_value_changes_inside_a_drawing_are_drawing_changes()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var code = NewCode();
        var created = await (await owner.PostAsync($"/api/zones/{zone.Id()}/rafts", RawRaft(NewRaftBody(code), Stored))).ReadAsync(HttpStatusCode.Created);
        var id = created.Id();
        var rowVersion = created.Text("rowVersion");

        var variants = new[]
        {
            "{\"label\":\"Bè ồ\",\"shapes\":[{\"kind\":\"rect\",\"x\":1,\"y\":2},{\"kind\":\"rect\",\"x\":3,\"y\":4}]}",
            "{\"label\":\"Bè ồ\",\"shapes\":[{\"kind\":\"rect\",\"x\":3,\"y\":4},{\"kind\":\"rect\",\"x\":1,\"y\":2}]}",
            "{\"label\":\"Bè ồ\",\"shapes\":[{\"kind\":\"rect\",\"x\":3,\"y\":4},{\"kind\":\"rect\",\"x\":1,\"y\":2.5}]}",
            "{\"label\":\"Bè ồ\",\"shapes\":[{\"kind\":\"rect\",\"x\":3,\"y\":4},{\"kind\":\"rect\",\"x\":1,\"y\":2.5}],\"extra\":null}",
        };

        var expectedChanges = 0;
        foreach (var variant in variants)
        {
            var body = await (await owner.PutAsync($"/api/rafts/{id}", RawRaft(NewRaftBody(code) with { RowVersion = rowVersion }, variant)))
                .ReadAsync(HttpStatusCode.OK);
            rowVersion = body.Text("rowVersion");
            expectedChanges++;
            Assert.Equal(expectedChanges, await DrawingChangesAsync(id));
            // A drawing-only change is written into the current layout version: no new version is created.
            Assert.Equal(1, await LayoutVersionsAsync(id));
        }
    }

    /// <summary>A lane kept only by a cultivation log entry is refused with MSG11 and the shrink changes nothing.</summary>
    [Fact]
    public async Task Refused_lane_removal_with_a_log_entry_changes_nothing()
    {
        var actor = await factory.OwnerAsync();
        var zone = await actor.Client.CreateZoneAsync();
        var raft = await actor.Client.CreateRaftAsync(zone.Id(), NewRaftBody(lanes: 5));
        await factory.UseDbAsync(db => db.CultivationLogEntries.Add(new CultivationLogEntry
        {
            ZoneId = zone.Id(),
            LaneId = raft.LaneId(5),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "Observation",
            RecordedByAccountId = actor.Account.Id,
        }));

        var shrink = await actor.Client.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code"), lanes: 4) with { RowVersion = raft.Text("rowVersion") });

        Assert.Equal(HttpStatusCode.Conflict, shrink.StatusCode);
        Assert.Equal("MSG-GEN-06", await shrink.ErrorCodeAsync());
        Assert.Equal(5, await factory.QueryAsync(db => db.Lanes.CountAsync(l => l.RaftId == raft.Id())));
        Assert.Equal(1, await LayoutVersionsAsync(raft.Id()));
    }

    /// <summary>Zone list paging: page 0 and negative pages become 1, pageSize is clamped to 1..100, huge pages are empty, not 500.</summary>
    [Fact]
    public async Task Zone_paging_clamps_page_and_page_size()
    {
        var owner = (await factory.OwnerAsync()).Client;

        var zero = await owner.GetFromJsonAsync<JsonElement>("/api/zones?page=0&pageSize=0");
        var negative = await owner.GetFromJsonAsync<JsonElement>("/api/zones?page=-3&pageSize=100000");
        var huge = await owner.GetAsync($"/api/zones?page={int.MaxValue}&pageSize=100");

        Assert.Equal(1, zero.GetProperty("page").GetInt32());
        Assert.Equal(1, zero.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, negative.GetProperty("page").GetInt32());
        Assert.Equal(100, negative.GetProperty("pageSize").GetInt32());
        Assert.Equal(HttpStatusCode.OK, huge.StatusCode);
        Assert.Empty((await huge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
    }

    /// <summary>Business dates are UTC+7: a cycle opened at 00:30 local on a day is open that day and not the day before.</summary>
    [Fact]
    public async Task Cycle_open_on_a_business_date_follows_local_midnight()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zone = await owner.CreateZoneAsync();
        var raft = await owner.CreateRaftAsync(zone.Id(), NewRaftBody());
        var day = Today.AddDays(-5);
        var opened = new DateTimeOffset(day.ToDateTime(new TimeOnly(0, 30)), BusinessClock.UtcOffset);
        var cycle = await factory.AddCycleAsync(raft.Id(), opened);

        var onDay = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition?date={day:yyyy-MM-dd}");
        var dayBefore = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition?date={day.AddDays(-1):yyyy-MM-dd}");

        Assert.Equal(cycle.Id, onDay.GetProperty("cycleId").GetGuid());
        Assert.Equal(JsonValueKind.Null, dayBefore.GetProperty("cycleId").ValueKind);
    }

    /// <summary>The reminder day runs at the business clock's 07:00 (UTC+7): 06:59 waits one minute, 07:00 waits a full day.</summary>
    [Theory]
    [InlineData("2026-10-22T23:59:00Z", 60)]
    [InlineData("2026-10-22T23:59:30Z", 30)]
    [InlineData("2026-10-23T00:00:00Z", 86400)]
    [InlineData("2026-10-22T16:00:00Z", 28800)]
    public void Reminder_delay_at_0659_and_0700_local_follows_the_business_clock(string utc, int expectedSeconds)
    {
        var delay = ZoneLicenseReminderHostedService.DelayUntilNextRun(DateTimeOffset.Parse(utc), new TimeOnly(7, 0));

        Assert.Equal((double)expectedSeconds, delay.TotalSeconds);
    }

    private Task<int> DrawingChangesAsync(Guid raftId) =>
        factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raftId && e.Action == "Raft.LayoutDrawingChanged"));

    private Task<int> LayoutVersionsAsync(Guid raftId) =>
        factory.QueryAsync(db => db.RaftLayoutVersions.CountAsync(v => v.RaftId == raftId));

    private Task<int> UpdatedCountAsync(Guid raftId) =>
        factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == raftId && e.Action == "Raft.Updated"));

    /// <summary>The raft body with its drawing written as the given raw JSON text (whitespace and escapes kept).</summary>
    internal static StringContent RawRaft(RaftBody body, string drawingJson)
    {
        var json = JsonSerializer.Serialize(body with { LayoutDrawing = null }, Web);
        const string placeholder = "\"layoutDrawing\":null";
        Assert.Equal(1, CountOf(json, placeholder));
        return new StringContent(json.Replace(placeholder, "\"layoutDrawing\":" + drawingJson), Encoding.UTF8, "application/json");
    }

    internal static int CountOf(string text, string part) => (text.Length - text.Replace(part, string.Empty).Length) / part.Length;
}

/// <summary>
/// Concurrency and reminder checks on SQL Server. A zone discontinue is committed by another connection in the
/// middle of the raft save (a deterministic interleaving), so the only way the save can succeed is if it ignores the zone row.
/// </summary>
public class ReviewFixQaSqlServerTests
{
    private const string Stored = "{\"label\":\"Bè ố\",\"shapes\":[{\"kind\":\"rect\",\"x\":1,\"y\":2},{\"kind\":\"rect\",\"x\":3,\"y\":4}]}";

    private const string SameVariant =
        "{ \"shapes\" : [ { \"y\" : 2, \"x\" : 1, \"kind\" : \"rect\" }, {\"y\":4,\"x\":3,\"kind\":\"rect\"} ],\n  \"label\" : \"B\\u00e8 \\u1ed1\" }";

    /// <summary>Raft create whose zone is discontinued between its read and its save is refused (MSG06) and leaves no raft.</summary>
    [SqlServerFact]
    public async Task Raft_create_racing_a_zone_discontinue_is_refused_and_leaves_no_raft()
    {
        await using var host = await ZoneRaceSqlHost.StartAsync();
        var owner = host.Owner;
        var zone = await owner.CreateZoneAsync();
        var zoneId = zone.Id();

        host.Race.Arm(zoneId);
        var response = await owner.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", NewRaftBody());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("MSG06", await response.ErrorCodeAsync());
        var (status, rafts) = await host.QueryAsync(async db => (
            (await db.FarmingZones.SingleAsync(z => z.Id == zoneId)).StatusId,
            await db.Rafts.CountAsync(r => r.ZoneId == zoneId)));
        Assert.Equal(StatusIds.Usage.Discontinued, status);
        Assert.Equal(0, rafts);
    }

    /// <summary>Raft reactivate whose zone is discontinued between its read and its save is refused (MSG06); the raft stays discontinued.</summary>
    [SqlServerFact]
    public async Task Raft_reactivate_racing_a_zone_discontinue_is_refused_and_the_raft_stays_discontinued()
    {
        await using var host = await ZoneRaceSqlHost.StartAsync();
        var owner = host.Owner;
        var zone = await owner.CreateZoneAsync();
        var zoneId = zone.Id();
        var raft = await owner.CreateRaftAsync(zoneId, NewRaftBody());
        var discontinued = await (await owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") }))
            .ReadAsync(HttpStatusCode.OK);

        host.Race.Arm(zoneId);
        var response = await owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/reactivate", new { rowVersion = discontinued.Text("rowVersion") });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("MSG06", await response.ErrorCodeAsync());
        var (zoneStatus, raftStatus) = await host.QueryAsync(async db => (
            (await db.FarmingZones.SingleAsync(z => z.Id == zoneId)).StatusId,
            (await db.Rafts.SingleAsync(r => r.Id == raft.Id())).StatusId));
        Assert.Equal(StatusIds.Usage.Discontinued, zoneStatus);
        Assert.Equal(StatusIds.Usage.Discontinued, raftStatus);
    }

    /// <summary>On SQL Server (real row versions): the same drawing written differently keeps the row version; a notes-only edit bumps it once.</summary>
    [SqlServerFact]
    public async Task Same_drawing_keeps_the_row_version_and_a_notes_edit_bumps_it_once_on_sql_server()
    {
        await using var host = await ZoneRaceSqlHost.StartAsync();
        var owner = host.Owner;
        var zone = await owner.CreateZoneAsync();
        var code = NewCode();
        var created = await (await owner.PostAsync($"/api/zones/{zone.Id()}/rafts", ReviewFixQaTests.RawRaft(NewRaftBody(code), Stored))).ReadAsync(HttpStatusCode.Created);
        var id = created.Id();

        var same = await (await owner.PutAsync($"/api/rafts/{id}", ReviewFixQaTests.RawRaft(NewRaftBody(code) with { RowVersion = created.Text("rowVersion") }, SameVariant)))
            .ReadAsync(HttpStatusCode.OK);
        var notes = await (await owner.PutAsync($"/api/rafts/{id}", ReviewFixQaTests.RawRaft(NewRaftBody(code) with
        {
            ConditionNotes = "Dây neo mới",
            RowVersion = same.Text("rowVersion"),
        }, SameVariant))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(created.Text("rowVersion"), same.Text("rowVersion"));
        Assert.NotEqual(same.Text("rowVersion"), notes.Text("rowVersion"));
        var drawingChanges = await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "Raft.LayoutDrawingChanged"));
        Assert.Equal(0, drawingChanges);
    }

    /// <summary>Control for the two race tests: without a parallel discontinue the same raft create succeeds on SQL Server.</summary>
    [SqlServerFact]
    public async Task Raft_create_without_a_race_succeeds_on_sql_server()
    {
        await using var host = await ZoneRaceSqlHost.StartAsync();
        var zone = await host.Owner.CreateZoneAsync();

        var raft = await host.Owner.CreateRaftAsync(zone.Id(), NewRaftBody());

        Assert.NotEqual(Guid.Empty, raft.Id());
        Assert.Equal(1, await host.QueryAsync(db => db.Rafts.CountAsync(r => r.ZoneId == zone.Id())));
    }

    /// <summary>
    /// Catch-up on start, on SQL Server: an expired license with no marker gets exactly one notice per recipient
    /// (the expired one), the markers are set, and a later run sends nothing.
    /// </summary>
    [SqlServerFact]
    public async Task Catch_up_on_start_sends_an_expired_license_once_and_a_second_run_sends_nothing()
    {
        await using var host = await ZoneRaceSqlHost.StartAsync();
        var zone = await host.Owner.CreateZoneAsync();
        var license = await host.Owner.CreateLicenseAsync(new LicenseFields
        {
            ZoneIds = [zone.Id()],
            EffectiveDate = Today.AddYears(-1),
            ExpiryDate = Today.AddDays(-2),
        });
        var licenseId = license.Id();
        var clock = new FixedClock(new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), BusinessClock.UtcOffset));
        var job = host.Api.Services.GetRequiredService<ZoneLicenseReminderJob>();
        var hosted = new ZoneLicenseReminderHostedService(job, Options.Create(new ZoneLicenseReminderOptions { CatchUpOnStart = true }), clock, NullLogger<ZoneLicenseReminderHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(async () => (await host.LicenseMarkersAsync(licenseId)).Expired is not null);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        var markers = await host.LicenseMarkersAsync(licenseId);
        Assert.NotNull(markers.Expiring);
        Assert.NotNull(markers.Expired);
        var notices = await host.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.ZoneLicenseExpiry && n.RelatedRecordId == licenseId)
            .Select(n => n.RecipientAccountId).ToListAsync());
        Assert.NotEmpty(notices);
        Assert.Equal(notices.Distinct().Count(), notices.Count);
        Assert.Equal(0, await job.RunOnceAsync(Today));
    }

    /// <summary>Candidate query on SQL Server: only unreplaced licenses with a notice due today are loaded.</summary>
    [SqlServerFact]
    public async Task Candidate_query_on_sql_server_returns_only_licenses_with_a_notice_due()
    {
        await using var host = await ZoneRaceSqlHost.StartAsync();
        var zone = await host.Owner.CreateZoneAsync();
        var zoneIds = new[] { zone.Id() };
        var due = (await host.Owner.CreateLicenseAsync(new LicenseFields { ZoneIds = zoneIds, EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(10) })).Id();
        var far = (await host.Owner.CreateLicenseAsync(new LicenseFields { ZoneIds = zoneIds, EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(90) })).Id();
        var told = (await host.Owner.CreateLicenseAsync(new LicenseFields { ZoneIds = zoneIds, EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(5) })).Id();
        var lapsedTold = (await host.Owner.CreateLicenseAsync(new LicenseFields { ZoneIds = zoneIds, EffectiveDate = Today.AddYears(-1), ExpiryDate = Today.AddDays(-3) })).Id();
        await host.UseDbAsync(async db =>
        {
            foreach (var document in await db.ZoneComplianceDocuments.Where(d => d.Id == told || d.Id == lapsedTold).ToListAsync())
            {
                document.ExpiringReminderSentAt = DateTimeOffset.UtcNow;
                document.ExpiredReminderSentAt = document.Id == lapsedTold ? DateTimeOffset.UtcNow : null;
            }
        });

        using var scope = host.Api.Services.CreateScope();
        var ids = await scope.ServiceProvider.GetRequiredService<ZoneLicenseReminderService>().CandidateIdsAsync(Today, default);

        Assert.Contains(due, ids);
        Assert.DoesNotContain(far, ids);
        Assert.DoesNotContain(told, ids);
        Assert.DoesNotContain(lapsedTold, ids);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The catch-up reminder did not run within 30 seconds.");
            }

            await Task.Delay(200);
        }
    }
}

/// <summary>A clock fixed at one instant (catch-up and business date tests).</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Set by a test: the next save of a raft in this zone first discontinues the zone from another connection.</summary>
internal sealed class ZoneRace
{
    private Guid? _armedZoneId;

    public void Arm(Guid zoneId) => _armedZoneId = zoneId;

    /// <summary>The armed zone when this save touches a raft of it (and disarms), otherwise null.</summary>
    public Guid? TakeIfAffected(OmtsDbContext db)
    {
        if (_armedZoneId is not { } zoneId)
        {
            return null;
        }

        var affected = db.ChangeTracker.Entries<Raft>()
            .Any(e => e.Entity.ZoneId == zoneId && (e.State == EntityState.Added || e.State == EntityState.Modified));
        if (!affected)
        {
            return null;
        }

        _armedZoneId = null;
        return zoneId;
    }
}

/// <summary>Unit of work of the race host: the production behaviour plus the armed race before the save.</summary>
internal sealed class RacingUnitOfWork(OmtsDbContext db, ZoneRace race, Func<Guid, Task> discontinueElsewhere) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (race.TakeIfAffected(db) is { } zoneId)
        {
            await discontinueElsewhere(zoneId);
        }

        try
        {
            return await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException(exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DuplicateRecordException(exception);
        }
    }

    public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) =>
        db.Entry(entity).ReloadAsync(cancellationToken);

    public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) =>
        db.Entry(entity).Property(e => e.RowVersion).OriginalValue = rowVersion;

    public void MarkChanged(AuditableEntity entity)
    {
        var entry = db.Entry(entity);
        if (entry.State == EntityState.Unchanged)
        {
            entry.State = EntityState.Modified;
        }
    }
}

/// <summary>The real API on a fresh migrated SQL Server database, with the racing unit of work and a logged-in Owner.</summary>
internal sealed class ZoneRaceApiFactory(SqlServerTestDatabase database) : WebApplicationFactory<Program>
{
    private readonly string _signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public ZoneRace Race { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OmtsDb"] = database.ConnectionString,
            ["Jwt:SigningKey"] = _signingKey,
            ["RateLimits:CodeRequestsPerMinute"] = "100000",
            ["RateLimits:LoginsPerMinute"] = "100000",
            ["Jobs:ZoneLicenseReminder:CatchUpOnStart"] = "false",
            ["Jobs:WaterMeasurementOverdue:CatchUpOnStart"] = "false",
            ["Jobs:OrphanFileCleanup:Enabled"] = "false",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new RacingUnitOfWork(
                provider.GetRequiredService<OmtsDbContext>(), Race, DiscontinueZoneElsewhereAsync));
        });
    }

    /// <summary>Discontinues the zone on its own connection and commit, as a parallel Owner session would.</summary>
    private async Task DiscontinueZoneElsewhereAsync(Guid zoneId)
    {
        await using var other = database.CreateContext();
        var zone = await other.FarmingZones.SingleAsync(z => z.Id == zoneId);
        zone.StatusId = StatusIds.Usage.Discontinued;
        await other.SaveChangesAsync();
    }
}

/// <summary>A migrated SQL Server database, a cooperative and a logged-in Owner on <see cref="ZoneRaceApiFactory"/>.</summary>
internal sealed class ZoneRaceSqlHost : IAsyncDisposable
{
    private ZoneRaceSqlHost(SqlServerTestDatabase database, ZoneRaceApiFactory api, HttpClient owner)
    {
        Database = database;
        Api = api;
        Owner = owner;
    }

    public SqlServerTestDatabase Database { get; }
    public ZoneRaceApiFactory Api { get; }
    public HttpClient Owner { get; }
    public ZoneRace Race => Api.Race;

    public static async Task<ZoneRaceSqlHost> StartAsync()
    {
        var database = SqlServerTestDatabase.Create();
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        var api = new ZoneRaceApiFactory(database);
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            db.Cooperatives.Add(new Cooperative { FullName = "HTX Trung Nam" });
            var owner = new Account
            {
                Username = NewCode(),
                DisplayName = "Owner",
                PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password),
                SubjectType = SubjectType.CooperativeOwner,
                StatusId = StatusIds.Account.Active,
            };
            db.Accounts.Add(owner);
            await db.SaveChangesAsync();
            var client = api.CreateClient();
            var tokens = await LoginTokensAsync(client, owner.Username);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
            return new ZoneRaceSqlHost(database, api, client);
        }
    }

    public async Task UseDbAsync(Func<OmtsDbContext, Task> change)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    public async Task<T> QueryAsync<T>(Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = Api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    public async Task<(DateTimeOffset? Expiring, DateTimeOffset? Expired)> LicenseMarkersAsync(Guid id) =>
        await QueryAsync(async db =>
        {
            var document = await db.ZoneComplianceDocuments.AsNoTracking().SingleAsync(d => d.Id == id);
            return (document.ExpiringReminderSentAt, document.ExpiredReminderSentAt);
        });

    public async ValueTask DisposeAsync()
    {
        Owner.Dispose();
        await Api.DisposeAsync();
        await Database.DisposeAsync();
    }
}
