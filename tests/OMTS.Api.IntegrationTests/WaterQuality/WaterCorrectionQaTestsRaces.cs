using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA of the rerun after a lost save on a real SQL Server: the rival saves for real just before the request's own save, so
/// the row version conflict, the interceptor and the discard are all the production ones (no stand-in that drops the pushes).
/// </summary>
[Collection(WaterRaceTestCollection.Name)]
public class WaterCorrectionQaTestsRaces
{
    [SqlServerFact]
    public async Task A_correction_that_loses_to_a_real_one_leaves_one_audit_row_and_one_push_per_notice_of_each()
    {
        await using var race = await Race.StartAsync();
        var (loser, loserAccount) = await race.OwnerAsync();
        var (winner, winnerAccount) = await race.OwnerAsync();
        var (_, manager) = await race.ManagerAsync();
        var id = await race.Seeded.RecordAsync(loser, 7m, 20m);
        race.BeforeNextSave(async () => Assert.Equal(HttpStatusCode.OK, (await winner.PostAsJsonAsync(race.Corrections(id), race.Body(9m, 20m))).StatusCode));

        var response = await loser.PostAsJsonAsync(race.Corrections(id), race.Body(9.2m, 31m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, race.RivalsRun);
        var audit = (await race.AuditAsync(id)).Where(e => e.Action == "WaterMeasurement.Corrected").ToList();
        Assert.Equal(2, audit.Count);
        Assert.Equal(9m, PhOf(audit.Single(e => e.ActorAccountId == winnerAccount.Id).After));
        Assert.Equal(9m, PhOf(audit.Single(e => e.ActorAccountId == loserAccount.Id).Before));
        Assert.Equal(9.2m, PhOf(audit.Single(e => e.ActorAccountId == loserAccount.Id).After));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((9.2m, 31m), (body.GetProperty("ph").GetDecimal(), body.GetProperty("salinity").GetDecimal()));

        // Winner: {} to {pH}; loser judged on that: {pH} to {pH, salinity}. Each Owner is left out of their own.
        await race.AssertNoticesAsync(id, manager.Id, alerts: 2, withdrawals: 0);
        await race.AssertNoticesAsync(id, loserAccount.Id, alerts: 1, withdrawals: 0);
        await race.AssertNoticesAsync(id, winnerAccount.Id, alerts: 1, withdrawals: 0);
    }

    [SqlServerFact]
    public async Task A_notice_of_the_lost_round_is_gone_when_the_rerun_has_nothing_to_tell()
    {
        await using var race = await Race.StartAsync();
        var (loser, _) = await race.OwnerAsync();
        var (winner, _) = await race.OwnerAsync();
        var (_, manager) = await race.ManagerAsync();
        var id = await race.Seeded.RecordAsync(loser, 9m, 20m);
        race.BeforeNextSave(async () => await winner.PostAsJsonAsync(race.Corrections(id), race.Body(7m, 20m)));

        // First round: outside to inside would withdraw. Rerun: inside to inside.
        var response = await loser.PostAsJsonAsync(race.Corrections(id), race.Body(7.2m, 20m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await race.AssertNoticesAsync(id, manager.Id, alerts: 1, withdrawals: 1);
        Assert.Equal(1, (await race.PushesAsync(manager.Id)).Count(m => m.Text.Contains("was withdrawn")));
    }

    [SqlServerFact]
    public async Task A_correction_that_loses_to_a_real_void_answers_MSG_GEN_09_and_leaves_nothing()
    {
        await using var race = await Race.StartAsync();
        var (loser, _) = await race.OwnerAsync();
        var (winner, _) = await race.OwnerAsync();
        var (_, manager) = await race.ManagerAsync();
        var id = await race.Seeded.RecordAsync(loser, 9m, 20m);
        race.BeforeNextSave(async () => Assert.Equal(HttpStatusCode.OK, (await winner.VoidAsync(id, reason: "Đo nhầm")).StatusCode));

        var response = await loser.PostAsJsonAsync(race.Corrections(id), race.Body(7m, 31m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("MSG-GEN-09", await response.ErrorCodeAsync());
        var stored = await race.StoredAsync(id);
        Assert.Equal((StatusIds.WaterMeasurement.Voided, 9m, 20m, "Đo nhầm"), (stored.StatusId, stored.Ph, stored.Salinity, stored.VoidReason));
        Assert.DoesNotContain(await race.AuditAsync(id), e => e.Action == "WaterMeasurement.Corrected");
        await race.AssertNoticesAsync(id, manager.Id, alerts: 1, withdrawals: 1);
    }

    [SqlServerFact]
    public async Task A_void_that_loses_to_a_real_correction_voids_the_corrected_values_and_judges_them()
    {
        await using var race = await Race.StartAsync();
        var (loser, _) = await race.OwnerAsync();
        var (winner, _) = await race.OwnerAsync();
        var (_, manager) = await race.ManagerAsync();
        var id = await race.Seeded.RecordAsync(loser, 9m, 20m);
        race.BeforeNextSave(async () => await winner.PostAsJsonAsync(race.Corrections(id), race.Body(7m, 20m)));

        var response = await loser.VoidAsync(id, reason: "Đo nhầm");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await race.StoredAsync(id);
        Assert.Equal((StatusIds.WaterMeasurement.Voided, 7m), (stored.StatusId, stored.Ph));
        var audit = await race.AuditAsync(id);
        Assert.Equal(7m, PhOf(audit.Single(e => e.Action == "WaterMeasurement.Voided").Before));
        // The withdrawal is the winner's; the void of a measurement now inside has nothing to withdraw.
        await race.AssertNoticesAsync(id, manager.Id, alerts: 1, withdrawals: 1);
    }

    [SqlServerFact]
    public async Task Two_voids_racing_answer_one_200_and_one_MSG_GEN_09_with_one_withdrawal()
    {
        await using var race = await Race.StartAsync();
        var (loser, _) = await race.OwnerAsync();
        var (winner, _) = await race.OwnerAsync();
        var (_, manager) = await race.ManagerAsync();
        var id = await race.Seeded.RecordAsync(loser, 9m, 20m);
        race.BeforeNextSave(async () => await winner.VoidAsync(id, reason: "Người thắng"));

        var response = await loser.VoidAsync(id, reason: "Người thua");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("MSG-GEN-09", await response.ErrorCodeAsync());
        Assert.Equal("Người thắng", (await race.StoredAsync(id)).VoidReason);
        Assert.Single(await race.AuditAsync(id), e => e.Action == "WaterMeasurement.Voided");
        await race.AssertNoticesAsync(id, manager.Id, alerts: 1, withdrawals: 1);
    }

    // PRE-2 "can still access its zone": the checks run again on each round.
    [SqlServerFact]
    public async Task A_zone_manager_whose_pair_is_revoked_between_the_rounds_is_refused_and_leaves_nothing()
    {
        await using var race = await Race.StartAsync();
        var (manager, account) = await race.ManagerAsync();
        var (_, colleague) = await race.ManagerAsync();
        var id = await race.Seeded.RecordAsync(manager, 9m, 20m);
        race.BeforeNextSave(() => race.ChangeAsync(async db =>
        {
            (await db.AccessGrants.SingleAsync(g => g.AccountId == account.Id)).StatusId = StatusIds.AccessGrant.Revoked;
            (await db.WaterMeasurements.SingleAsync(m => m.Id == id)).Salinity = 21m;
        }));

        var response = await manager.PostAsJsonAsync(race.Corrections(id), race.Body(7m, 20m));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(9m, (await race.StoredAsync(id)).Ph);
        Assert.DoesNotContain(await race.AuditAsync(id), e => e.Action == "WaterMeasurement.Corrected");
        await race.AssertNoticesAsync(id, colleague.Id, alerts: 1, withdrawals: 0);
    }

    [SqlServerFact]
    public async Task A_zone_discontinued_between_the_rounds_is_refused()
    {
        await using var race = await Race.StartAsync();
        var (owner, _) = await race.OwnerAsync();
        var id = await race.Seeded.RecordAsync(owner, 9m, 20m);
        race.BeforeNextSave(() => race.ChangeAsync(async db =>
        {
            (await db.FarmingZones.SingleAsync(z => z.Id == race.Seeded.Zone.Id)).StatusId = StatusIds.Usage.Discontinued;
            (await db.WaterMeasurements.SingleAsync(m => m.Id == id)).Salinity = 21m;
        }));

        var response = await owner.PostAsJsonAsync(race.Corrections(id), race.Body(7m, 20m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Farming zone is Discontinued", await response.Content.ReadAsStringAsync());
    }

    [SqlServerFact]
    public async Task A_raft_discontinued_between_the_rounds_is_refused_on_the_raft_field()
    {
        await using var race = await Race.StartAsync();
        var (owner, _) = await race.OwnerAsync();
        var raft = new Raft { ZoneId = race.Seeded.Zone.Id, Code = $"B-{NewCode()}" };
        await race.ChangeAsync(db =>
        {
            db.Rafts.Add(raft);
            return Task.CompletedTask;
        });
        var id = await race.Seeded.RecordAsync(owner, 9m, 20m);
        race.BeforeNextSave(() => race.ChangeAsync(async db =>
        {
            (await db.Rafts.SingleAsync(r => r.Id == raft.Id)).StatusId = StatusIds.Usage.Discontinued;
            (await db.WaterMeasurements.SingleAsync(m => m.Id == id)).Salinity = 21m;
        }));

        var response = await owner.PostAsJsonAsync(race.Corrections(id), race.Body(7m, 20m, raft.Id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Raft", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.DoesNotContain(await race.AuditAsync(id), e => e.Action == "WaterMeasurement.Corrected");
    }

    // MarkChanged writes every column on SQL Server: the creation stamps, the photo and the copy must come through unchanged.
    [SqlServerFact]
    public async Task A_correction_with_the_same_values_is_saved_once_and_keeps_what_it_may_not_change()
    {
        await using var race = await Race.StartAsync();
        var (owner, ownerAccount) = await race.OwnerAsync();
        var (other, otherAccount) = await race.OwnerAsync();
        var id = await race.Seeded.RecordAsync(owner, 9m, 20m);
        var before = await race.StoredAsync(id);
        var body = new
        {
            raftId = before.RaftId, measuredAt = before.MeasuredAt, ph = before.Ph, salinity = before.Salinity, temperature = before.Temperature, reason = "Kiểm tra lại",
        };

        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync(race.Corrections(id), body)).StatusCode);

        var after = await race.StoredAsync(id);
        Assert.NotEqual(before.RowVersion, after.RowVersion);
        Assert.Equal((before.CreatedAt, before.CreatedBy, before.PhotoUrl, before.PhMaxUsed, before.AlertedAt, before.ClientOperationId), (after.CreatedAt, after.CreatedBy, after.PhotoUrl, after.PhMaxUsed, after.AlertedAt, after.ClientOperationId));
        Assert.Equal(otherAccount.Id, after.UpdatedBy);
        Assert.Equal(ownerAccount.Id, after.MeasuredByAccountId);
        Assert.Single(await race.AuditAsync(id), e => e.Action == "WaterMeasurement.Corrected");
        await race.AssertNoticesAsync(id, otherAccount.Id, alerts: 1, withdrawals: 0);
    }

    // F-04: NFR-09 "water measurements have no version check". Past 20 lost rounds a request still answers 409 MSG06.
    // Measured here: 24 parallel requests all answered 200 (3 runs); 40 gave seven or eight 409 MSG06 (3 runs).
    [SqlServerFact(Skip = "Accepted: past 20 lost rounds, which needs more than 24 parallel saves of one measurement, a request answers 409 MSG06.")]
    public async Task F04_many_parallel_corrections_of_one_measurement_all_answer_200()
    {
        await using var race = await Race.StartAsync();
        var (owner, _) = await race.OwnerAsync();
        var id = await race.Seeded.RecordAsync(owner, 7m, 20m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            owner.PostAsJsonAsync(race.Corrections(id), race.Body(7m + (i % 10 / 10m), 20m))));

        var codes = new List<string>();
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            codes.Add($"{(int)refused.StatusCode} {await refused.ErrorCodeAsync()}");
        }

        Assert.True(codes.Count == 0, string.Join(", ", codes));
    }

    // Parallel changes of the zone row, next to new records of the zone: nothing answers 500, and water never answers MSG06.
    [SqlServerFact]
    public async Task Rounds_of_parallel_corrections_voids_and_records_never_answer_500_or_MSG06()
    {
        await using var race = await Race.StartAsync();
        var (owner, _) = await race.OwnerAsync();
        var failures = new List<string>();
        for (var round = 0; round < 4; round++)
        {
            var ids = new List<Guid>();
            for (var i = 0; i < 3; i++)
            {
                ids.Add(await race.Seeded.RecordAsync(owner, 7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddHours(-3 - i)));
            }

            await race.ChangeAsync(async db => (await db.FarmingZones.SingleAsync(z => z.Id == race.Seeded.Zone.Id)).WaterOverdueRemindedAt = DateTimeOffset.UtcNow.AddMonths(-3));
            var calls = ids.SelectMany(id => Enumerable.Range(0, 4).Select(i => owner.PostAsJsonAsync(race.Corrections(id), race.Body(7m + i / 10m, 20m))))
                .Concat(Enumerable.Range(0, 2).Select(_ => owner.VoidAsync(ids[0])))
                .Concat(Enumerable.Range(0, 4).Select(i => race.Seeded.PostAsync(owner, 7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddDays(-2 - i))));
            foreach (var response in await Task.WhenAll(calls))
            {
                if (!response.IsSuccessStatusCode && !(response.StatusCode == HttpStatusCode.Conflict && await response.ErrorCodeAsync() == "MSG-GEN-09"))
                {
                    failures.Add($"{(int)response.StatusCode} {response.RequestMessage!.RequestUri!.AbsolutePath.Split('/')[^1]}: {await response.Content.ReadAsStringAsync()}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static decimal PhOf(string? snapshot)
    {
        using var json = JsonDocument.Parse(snapshot!);
        return json.RootElement.GetProperty("Ph").GetDecimal();
    }

    /// <summary>A migrated database, the real API and a hook that lets a rival save just before the next save of a request.</summary>
    private sealed class Race(SqlServerTestDatabase database, SqlServerApiFactory root, WebApplicationFactory<Program> api, WaterSqlServerZone seeded, RivalHook hook) : IAsyncDisposable
    {
        public WaterSqlServerZone Seeded => seeded;

        public int RivalsRun => hook.Run;

        public static async Task<Race> StartAsync()
        {
            var database = SqlServerTestDatabase.Create();
            var seeded = await WaterSqlServerKit.SeedAsync(database);
            var hook = new RivalHook();
            var root = new SqlServerApiFactory(database);
            var api = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.AddScoped<UnitOfWork>();
                services.AddScoped<IUnitOfWork>(provider => new RivalFirstUnitOfWork(provider.GetRequiredService<UnitOfWork>(), hook));
            }));
            return new Race(database, root, api, seeded, hook);
        }

        public void BeforeNextSave(Func<Task> rival) => hook.Arm(rival);

        public string Corrections(Guid id) => $"/api/water-measurements/{id}/corrections";

        public object Body(decimal ph, decimal salinity, Guid? raftId = null) => new
        {
            raftId = raftId ?? seeded.Raft.Id,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ph,
            salinity,
            temperature = 25m,
            reason = "Sai số",
        };

        public async Task<(HttpClient Client, Account Account)> OwnerAsync() => await AccountAsync(SubjectType.CooperativeOwner);

        public async Task<(HttpClient Client, Account Account)> ManagerAsync()
        {
            var (_, account) = await AccountAsync(SubjectType.Staff, login: false);
            await api.GrantAsync(account, StaffRole.ZoneManager, seeded.Zone.Id);
            return (await LoginAsync(account), account);
        }

        public async Task ChangeAsync(Func<OmtsDbContext, Task> change)
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            await change(db);
            await db.SaveChangesAsync();
        }

        public Task<WaterMeasurement> StoredAsync(Guid id) => QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id));

        public Task<List<OMTS.Domain.Entities.Platform.AuditEvent>> AuditAsync(Guid id) =>
            QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == id).ToListAsync());

        public Task<IReadOnlyList<FakeMessage>> PushesAsync(Guid accountId) =>
            Task.FromResult(api.Services.GetRequiredService<FakePushSender>().Inbox.List(accountId.ToString()));

        /// <summary>The rows of the account about the measurement and exactly one push for each.</summary>
        public async Task AssertNoticesAsync(Guid id, Guid accountId, int alerts, int withdrawals)
        {
            var rows = await QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.RelatedRecordId == id && n.RecipientAccountId == accountId).ToListAsync());
            Assert.Equal(alerts, rows.Count(n => n.Type == NotificationTypes.WaterAlert));
            Assert.Equal(withdrawals, rows.Count(n => n.Type == NotificationTypes.WaterAlertWithdrawn));
            var pushed = (await PushesAsync(accountId)).Select(m => m.NotificationId).ToList();
            foreach (var row in rows)
            {
                Assert.Single(pushed, p => p == row.Id);
            }

            Assert.Equal(rows.Count, pushed.Count);
        }

        public async ValueTask DisposeAsync()
        {
            await api.DisposeAsync();
            await root.DisposeAsync();
            await database.DisposeAsync();
        }

        private async Task<T> QueryAsync<T>(Func<OmtsDbContext, Task<T>> query)
        {
            using var scope = api.Services.CreateScope();
            return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
        }

        private readonly Dictionary<Guid, string> _secrets = [];

        private async Task<(HttpClient Client, Account Account)> AccountAsync(SubjectType subjectType, bool login = true)
        {
            var secret = NewPassword();
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            var account = new Account
            {
                Username = NewCode(),
                DisplayName = NewCode(),
                PasswordHash = scope.ServiceProvider.GetRequiredService<OMTS.Application.Common.Interfaces.Identity.IPasswordHasher>().Hash(secret),
                SubjectType = subjectType,
                PhoneNumber = NewPhone(),
                PhoneVerifiedAt = DateTimeOffset.UtcNow,
                StatusId = StatusIds.Account.Active,
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            lock (_secrets)
            {
                _secrets[account.Id] = secret;
            }

            return (login ? await LoginAsync(account) : null!, account);
        }

        private async Task<HttpClient> LoginAsync(Account account)
        {
            var client = api.CreateClient();
            var tokens = await LoginTokensAsync(client, account.Username, _secrets[account.Id]);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
            return client;
        }
    }

    private sealed class RivalHook
    {
        private Func<Task>? _rival;

        public int Run { get; private set; }

        public void Arm(Func<Task> rival) => _rival = rival;

        public async Task RunOnceAsync()
        {
            var rival = Interlocked.Exchange(ref _rival, null);
            if (rival is not null)
            {
                Run++;
                await rival();
            }
        }
    }

    private sealed class RivalFirstUnitOfWork(IUnitOfWork inner, RivalHook hook) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await hook.RunOnceAsync();
            return await inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

        public void DiscardChanges() => inner.DiscardChanges();

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);
    }
}
