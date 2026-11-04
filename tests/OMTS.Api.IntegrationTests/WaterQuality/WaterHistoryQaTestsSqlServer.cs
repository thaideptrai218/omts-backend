using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
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

        var runs = await Task.WhenAll(RunAsync(api, bothHaveRead), RunAsync(api, bothHaveRead));

        Assert.Equal([-1, 1], runs.Order());
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.Type == NotificationTypes.WaterMeasurementOverdue));
        Assert.Single(api.Services.GetRequiredService<FakePushSender>().Inbox.List(manager.Id.ToString()));
    }

    // Finding L-3: the run saves every zone in one transaction. A zone form saved between the read and the save fails the whole
    // run (the PM accepted the conflict for the form, not for the job), and the job's next try is tomorrow (DailyJobHostedService).
    // So the other zone, which nobody touched, gets its GFR-04 "every day" check a day late.
    [SqlServerFact]
    public async Task QaL3_a_zone_saved_during_the_run_does_not_cost_the_other_zones_their_reminder()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        var other = new FarmingZone { CooperativeId = seeded.Zone.CooperativeId, Code = $"Z{NewCode()}", Name = "Other" };
        var otherRaft = new Raft { Zone = other, Code = $"B-{NewCode()}" };
        await using (var db = database.CreateContext())
        {
            db.Rafts.Add(otherRaft);
            await db.SaveChangesAsync();
        }

        await AddOpenCycleAsync(database, seeded.Raft.Id);
        await AddOpenCycleAsync(database, otherRaft.Id);
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync($"o{NewCode()}", NewPassword());

        var run = await RunAsync(api, new Rendezvous(1), onFirstZone: async () =>
        {
            await using var elsewhere = database.CreateContext();
            (await elsewhere.FarmingZones.SingleAsync(z => z.Id == seeded.Zone.Id)).Name = "Saved by the zone form";
            await elsewhere.SaveChangesAsync();
        });

        await using var read = database.CreateContext();
        Assert.True(
            await read.Notifications.AnyAsync(n => n.Type == NotificationTypes.WaterMeasurementOverdue && n.RelatedRecordId == other.Id),
            $"The run answered {run} and the untouched zone got nothing until tomorrow.");
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

    /// <summary>One run in its own scope, stopping at the first zone until every run has read; -1 when it lost on the row version.</summary>
    private static async Task<int> RunAsync(SqlServerApiFactory api, Rendezvous rendezvous, Func<Task>? onFirstZone = null)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var reminder = new WaterMeasurementOverdueReminder(
            services.GetRequiredService<IWaterMeasurementRepository>(),
            services.GetRequiredService<ICultivationCycleQuery>(),
            new GatedRecipients(services.GetRequiredService<IZoneRecipientQuery>(), rendezvous, onFirstZone),
            services.GetRequiredService<INotificationDispatcher>(),
            services.GetRequiredService<IUnitOfWork>());
        try
        {
            return await reminder.RunOnceAsync(Today, CancellationToken.None);
        }
        catch (ConcurrencyConflictException)
        {
            return -1;
        }
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

    private sealed class GatedRecipients(IZoneRecipientQuery inner, Rendezvous rendezvous, Func<Task>? onFirstZone) : IZoneRecipientQuery
    {
        private bool _gated;

        public Task<IReadOnlyList<Guid>> GetRecipientsAsync(Guid zoneId, string functionCode, PermissionAction action, CancellationToken cancellationToken) =>
            inner.GetRecipientsAsync(zoneId, functionCode, action, cancellationToken);

        public async Task<IReadOnlyList<Guid>> GetZoneManagersOrOwnersAsync(Guid zoneId, CancellationToken cancellationToken)
        {
            if (!_gated)
            {
                _gated = true;
                await rendezvous.ArriveAsync();
                if (onFirstZone is not null)
                {
                    await onFirstZone();
                }
            }

            return await inner.GetZoneManagersOrOwnersAsync(zoneId, cancellationToken);
        }
    }
}
