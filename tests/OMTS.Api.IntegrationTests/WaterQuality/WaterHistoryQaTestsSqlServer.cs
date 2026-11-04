using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence.Repositories;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA on SQL Server: parallel runs of the GFR-04 job on the row version, and the order of the list and the file on the real keys.</summary>
public class WaterHistoryQaTestsSqlServer
{
    private static readonly DateOnly Today = TimeProvider.System.Today();

    // Both runs have read the zone before either saves: the row version lets one through, and the other sends nothing, not even its push.
    [SqlServerFact]
    public async Task Two_runs_at_the_same_moment_send_one_reminder_and_one_push()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var manager = await api.AddAccountAsync($"m{NewCode()}", NewPassword(), SubjectType.Staff);
        await api.GrantAsync(manager, StaffRole.ZoneManager, seeded.Zone.Id);
        await AddOpenCycleAsync(database, seeded.Raft.Id);
        var bothHaveRead = new Rendezvous(2);
        var host = WithHookBeforeRecipients(api, _ => bothHaveRead.ArriveAsync());
        var job = host.Services.GetRequiredService<WaterMeasurementOverdueJob>();

        var runs = await Task.WhenAll(job.RunOnceAsync(Today), job.RunOnceAsync(Today));

        Assert.Equal([0, 1], runs.Order());
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.Type == NotificationTypes.WaterMeasurementOverdue));
        Assert.Single(host.Services.GetRequiredService<FakePushSender>().Inbox.List(manager.Id.ToString()));
    }

    // GFR-04 "every day" holds per zone. A zone form saved between the job's read of a zone and its save (the PM accepted that
    // conflict for the form) costs that zone alone, wherever it stands in the run: nothing of the lost try stays, neither row,
    // marker nor push, so the next day sends its reminder once and the zones already reminded get nothing more.
    [SqlServerFact]
    public async Task A_zone_saved_during_the_run_loses_only_its_own_reminder_and_gets_it_once_the_next_day()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        var before = new FarmingZone { CooperativeId = seeded.Zone.CooperativeId, Code = $"A{NewCode()}", Name = "Before" };
        var after = new FarmingZone { CooperativeId = seeded.Zone.CooperativeId, Code = $"Z{NewCode()}", Name = "After" };
        var beforeRaft = new Raft { Zone = before, Code = $"B-{NewCode()}" };
        var afterRaft = new Raft { Zone = after, Code = $"B-{NewCode()}" };
        await using (var db = database.CreateContext())
        {
            (await db.FarmingZones.SingleAsync(z => z.Id == seeded.Zone.Id)).Code = $"M{NewCode()}";
            db.Rafts.AddRange(beforeRaft, afterRaft);
            await db.SaveChangesAsync();
        }

        await AddOpenCycleAsync(database, seeded.Raft.Id);
        await AddOpenCycleAsync(database, beforeRaft.Id);
        await AddOpenCycleAsync(database, afterRaft.Id);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync($"o{NewCode()}", NewPassword());
        var formSaved = false;
        var host = WithHookBeforeRecipients(api, async zoneId =>
        {
            if (zoneId == seeded.Zone.Id && !formSaved)
            {
                formSaved = true;
                await using var elsewhere = database.CreateContext();
                (await elsewhere.FarmingZones.SingleAsync(z => z.Id == seeded.Zone.Id)).Name = "Saved by the zone form";
                await elsewhere.SaveChangesAsync();
            }
        });
        var job = host.Services.GetRequiredService<WaterMeasurementOverdueJob>();
        var pushes = host.Services.GetRequiredService<FakePushSender>().Inbox;

        Assert.Equal(2, await job.RunOnceAsync(Today));

        Assert.True(formSaved);
        Assert.Equal(1, await RemindersOfAsync(database, before.Id));
        Assert.Equal(1, await RemindersOfAsync(database, after.Id));
        Assert.Equal(0, await RemindersOfAsync(database, seeded.Zone.Id));
        Assert.Equal(2, pushes.List(owner.Id.ToString()).Count);
        Assert.Contains(api.Logs.Lines, l => l.Contains($"failed for zone {seeded.Zone.Id}", StringComparison.Ordinal));
        await using (var read = database.CreateContext())
        {
            var saved = await read.FarmingZones.AsNoTracking().SingleAsync(z => z.Id == seeded.Zone.Id);
            Assert.Equal(("Saved by the zone form", (DateTimeOffset?)null), (saved.Name, saved.WaterOverdueRemindedAt));
        }

        Assert.Equal(1, await job.RunOnceAsync(Today.AddDays(1)));
        Assert.Equal(0, await job.RunOnceAsync(Today.AddDays(1)));

        Assert.Equal(1, await RemindersOfAsync(database, seeded.Zone.Id));
        Assert.Equal(1, await RemindersOfAsync(database, before.Id));
        Assert.Equal(1, await RemindersOfAsync(database, after.Id));
        Assert.Equal(3, pushes.List(owner.Id.ToString()).Count);
    }

    [SqlServerFact]
    public async Task The_file_is_the_list_in_its_order_when_time_and_save_are_the_same()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        var at = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds());
        await using (var db = database.CreateContext())
        {
            db.WaterMeasurements.AddRange(Enumerable.Range(0, 30).Select(i => new WaterMeasurement
            {
                ZoneId = seeded.Zone.Id,
                RaftId = seeded.Raft.Id,
                MeasuredAt = at,
                Ph = 7m,
                Salinity = 20m,
                Temperature = 10m + i,
                PhotoUrl = Guid.NewGuid().ToString("N"),
                ClientOperationId = Guid.NewGuid(),
            }));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE [WaterMeasurements] SET [CreatedAt] = {at}");
        }

        await using var api = new SqlServerApiFactory(database);
        var secret = NewPassword();
        var owner = await api.AddAccountAsync($"o{NewCode()}", secret);
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, owner.Username, secret);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var based = $"/api/zones/{seeded.Zone.Id}/water-measurements";

        var listed = new List<decimal>();
        for (var page = 1; page <= 3; page++)
        {
            var body = await client.GetFromJsonAsync<JsonElement>($"{based}?pageSize=10&page={page}");
            listed.AddRange(body.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("temperature").GetDecimal()));
        }

        using var workbook = new XLWorkbook(await (await client.GetAsync($"{based}/export")).Content.ReadAsStreamAsync());

        Assert.Equal(30, listed.Distinct().Count());
        Assert.Equal(listed, workbook.Worksheet("Measurements").RowsUsed().Skip(1).Select(r => r.Cell(5).GetValue<decimal>()));
    }

    /// <summary>The same API with <paramref name="hook"/> called when the job, having read a zone, asks for its recipients.</summary>
    private static WebApplicationFactory<Program> WithHookBeforeRecipients(SqlServerApiFactory api, Func<Guid, Task> hook) =>
        api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<ZoneRecipientQuery>();
            services.AddScoped<IZoneRecipientQuery>(provider => new HookedRecipients(provider.GetRequiredService<ZoneRecipientQuery>(), hook));
        }));

    private static async Task<int> RemindersOfAsync(SqlServerTestDatabase database, Guid zoneId)
    {
        await using var db = database.CreateContext();
        return await db.Notifications.CountAsync(n => n.Type == NotificationTypes.WaterMeasurementOverdue && n.RelatedRecordId == zoneId);
    }

    private static async Task AddOpenCycleAsync(SqlServerTestDatabase database, Guid raftId)
    {
        await using var db = database.CreateContext();
        db.CultivationCycles.Add(WaterOverdueTestKit.OpenCycle(raftId, Today.AddMonths(-2)));
        await db.SaveChangesAsync();
    }

    private sealed class Rendezvous(int runs)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == runs)
            {
                _all.TrySetResult();
            }

            return _all.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class HookedRecipients(IZoneRecipientQuery inner, Func<Guid, Task> hook) : IZoneRecipientQuery
    {
        public Task<IReadOnlyList<Guid>> GetRecipientsAsync(Guid zoneId, string functionCode, PermissionAction action, CancellationToken cancellationToken) =>
            inner.GetRecipientsAsync(zoneId, functionCode, action, cancellationToken);

        public async Task<IReadOnlyList<Guid>> GetZoneManagersOrOwnersAsync(Guid zoneId, CancellationToken cancellationToken)
        {
            await hook(zoneId);
            return await inner.GetZoneManagersOrOwnersAsync(zoneId, cancellationToken);
        }
    }
}
