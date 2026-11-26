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

        var response = await manager.VoidAsync(id, reason: "Đo nhầm");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StatusIds.WaterMeasurement.Active, (await race.StoredAsync(id)).StatusId);
        Assert.DoesNotContain(await race.AuditAsync(id), e => e.Action == "WaterMeasurement.Voided");
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

        var response = await owner.VoidAsync(id, reason: "Đo nhầm");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Farming zone is Discontinued", await response.Content.ReadAsStringAsync());
    }

    // Parallel voids of the records, next to new records of the zone: nothing answers 500, and water never answers MSG06.
    [SqlServerFact]
    public async Task Rounds_of_parallel_voids_and_records_never_answer_500_or_MSG06()
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

            var calls = ids.SelectMany(id => Enumerable.Range(0, 3).Select(_ => owner.VoidAsync(id)))
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
