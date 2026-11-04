using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>
/// QA of the commit rule and of "exactly one SMS per recipient per notification" on a real SQL Server: parallel requests and
/// job runs, a concurrency conflict retried by reload, a real deadlock, a cancellation on either side of the commit.
/// </summary>
public class MandatorySmsQaSqlServerTests
{
    private static NotificationContent Withdrawn() => new(NotificationTypes.WaterAlertWithdrawn, "Title", new UserMessage("MSG-WATER-04", "Withdrawn."));

    private static object Measurement(Guid? operation = null, bool confirm = true) =>
        new { clientOperationId = operation ?? Guid.NewGuid(), measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5), ph = 9.2m, salinity = 20m, confirm };

    private static string[] Statuses(HttpResponseMessage[] responses) => responses.Select(r => ((int)r.StatusCode).ToString()).ToArray();

    [SqlServerFact]
    public async Task Parallel_confirms_of_one_draft_send_one_sms_per_recipient()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());
        var (manager, client) = await ManagerAsync(api, zone.Id);
        var draftResponse = await client.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements", Measurement(confirm: false));
        var draftId = (await draftResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PostAsJsonAsync($"/api/water-measurements/{draftId}/confirm", new { })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, r => Assert.True((int)r.StatusCode < 500, string.Join(",", Statuses(responses))));
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id && n.Type == NotificationTypes.WaterAlert));
        Assert.Single(api.SmsTo(owner));
        Assert.Single(api.SmsTo(manager));
    }

    [SqlServerFact]
    public async Task Parallel_corrections_of_one_measurement_send_one_sms_per_recipient()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());
        var (manager, client) = await ManagerAsync(api, zone.Id);
        var recorded = await client.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements", Measurement());
        var id = (await recorded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Single(api.SmsTo(owner));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => client.PostAsJsonAsync(
            $"/api/water-measurements/{id}/corrections",
            new { ph = 9.3m + (i / 100m), salinity = 20m, measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5), reason = "Wrong reading", recordVersion = 1 })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True((int)r.StatusCode < 500, string.Join(",", Statuses(responses))));
        Assert.Equal(2, api.SmsTo(owner).Count);
        Assert.Equal(2, api.SmsTo(manager).Count);
    }

    [SqlServerFact]
    public async Task Parallel_different_measurements_send_one_sms_each()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());
        var (manager, client) = await ManagerAsync(api, zone.Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements", Measurement())));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(5, api.SmsTo(owner).Count);
        Assert.Equal(5, api.SmsTo(manager).Count);
    }

    // Two server instances, or a catch-up at start during the daily run: the row version keeps one notice.
    [SqlServerFact]
    public async Task Parallel_runs_of_the_license_job_send_one_sms()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());
        var today = BusinessClock.ToBusinessDate(DateTimeOffset.UtcNow);
        await using (var seed = database.CreateContext())
        {
            seed.ZoneComplianceDocuments.Add(new ZoneComplianceDocument
            {
                DocumentType = "AQUACULTURE_LICENSE", DocumentNumber = "GP-RACE", EffectiveDate = today.AddDays(-300), ExpiryDate = today.AddDays(10),
                ZoneScopes = [new DocumentZoneScope { ZoneId = zone.Id }],
            });
            await seed.SaveChangesAsync();
        }

        var job = api.Services.GetRequiredService<ZoneLicenseReminderJob>();
        var sent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => job.RunOnceAsync(today))));

        Assert.Equal(1, sent.Sum());
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id && n.Type == NotificationTypes.ZoneLicenseExpiry));
        Assert.Single(api.SmsTo(owner));
    }

    [SqlServerFact]
    public async Task A_concurrency_conflict_sends_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());

        using var scope = api.Services.CreateScope();
        await ConflictedSaveAsync(database, scope, zone.Id, owner.Id);

        await using var db = database.CreateContext();
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id));
        Assert.Empty(api.AllSms());
    }

    // EF does not raise SaveChangesFailed for a concurrency conflict, so the queue stays and goes out with the retry that commits the rows.
    // AuthService, CurrentPasswordProof and VerificationService retry like this; none of them notifies today.
    [SqlServerFact]
    public async Task A_conflict_retried_by_reload_sends_the_sms_of_the_notification_it_commits_once()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());

        using var scope = api.Services.CreateScope();
        var tracked = await ConflictedSaveAsync(database, scope, zone.Id, owner.Id);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.ReloadAsync(tracked);
        await unitOfWork.SaveChangesAsync();

        await using var db = database.CreateContext();
        var rows = await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id);
        Assert.Equal((1, 1), (rows, api.SmsTo(owner).Count));
    }

    [SqlServerFact]
    public async Task A_save_that_is_the_victim_of_a_real_deadlock_sends_nothing_and_keeps_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());
        var (firstId, secondId) = await TwoAccountsInSaveOrderAsync(api);

        using var scope = api.Services.CreateScope();
        var error = await DeadlockedSaveAsync(database, scope, owner.Id, firstId, secondId);

        Assert.Contains("deadlock victim", error.ToString());
        await using var db = database.CreateContext();
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id));
        Assert.Empty(api.AllSms());
    }

    // Unlike the conflict above: this failure drops the queue, while the tracker keeps the rows, so the retry commits a notification with no SMS and no push.
    [SqlServerFact]
    public async Task L4_a_deadlock_retried_in_the_same_request_still_sends_the_sms_of_the_notification_it_commits()
    {
        await using var database = SqlServerTestDatabase.Create();
        await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());
        var (firstId, secondId) = await TwoAccountsInSaveOrderAsync(api);

        using var scope = api.Services.CreateScope();
        await DeadlockedSaveAsync(database, scope, owner.Id, firstId, secondId);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        await using var db = database.CreateContext();
        var rows = await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id);
        Assert.Equal((1, 1), (rows, api.SmsTo(owner).Count));
    }

    // The request is aborted between the writes and the commit: nothing committed, nothing announced.
    [SqlServerFact]
    public async Task A_request_aborted_just_before_the_commit_sends_nothing_and_keeps_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        var hook = new CommitHook();
        await using var api = new SqlServerApiFactory(database);
        await using var hooked = Hooked(api, database, hook);
        var (owner, otherOwner) = (await api.AddAccountAsync(NewCode(), NewPassword()), await api.AddAccountAsync(NewCode(), NewPassword()));
        using var abort = new CancellationTokenSource();
        hook.BeforeCommit = abort.Cancel;

        using var scope = hooked.Services.CreateScope();
        await NotifyWithAZoneChangeAsync(scope, zone.Id, owner.Id, otherOwner.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(abort.Token));

        Assert.True(hook.CommitReached);
        await using var db = database.CreateContext();
        Assert.Equal(0, await db.Notifications.CountAsync());
        Assert.Empty(hooked.AllSms());
    }

    // The request is aborted right after the commit: the data stands, so does its SMS.
    [SqlServerFact]
    public async Task A_request_aborted_just_after_the_commit_keeps_the_data_and_loses_no_sms()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        var hook = new CommitHook();
        await using var api = new SqlServerApiFactory(database);
        await using var hooked = Hooked(api, database, hook);
        var (owner, otherOwner) = (await api.AddAccountAsync(NewCode(), NewPassword()), await api.AddAccountAsync(NewCode(), NewPassword()));
        using var abort = new CancellationTokenSource();
        hook.AfterCommit = abort.Cancel;

        using var scope = hooked.Services.CreateScope();
        await NotifyWithAZoneChangeAsync(scope, zone.Id, owner.Id, otherOwner.Id);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(abort.Token);

        Assert.True(hook.CommitReached);
        Assert.True(abort.IsCancellationRequested);
        await using var db = database.CreateContext();
        Assert.Equal(2, await db.Notifications.CountAsync());
        Assert.Equal(2, hooked.AllSms().Count);
    }

    // Read from another connection inside the provider call: the rows of the same save are already visible, so the send is after the commit.
    [SqlServerFact]
    public async Task The_rows_of_the_save_are_committed_when_the_provider_is_called()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        var seen = new List<int>();
        var reader = new MandatorySmsQaKit.RecordingSmsSender(_ =>
        {
            using var db = database.CreateContext();
            db.Database.SetCommandTimeout(5);
            lock (seen)
            {
                seen.Add(db.Notifications.Count());
            }

            return null;
        });
        await using var api = new SqlServerApiFactory(database);
        await using var hooked = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<OMTS.Application.Common.Interfaces.Messaging.ISmsSender>(reader)));
        var (owner, otherOwner) = (await api.AddAccountAsync(NewCode(), NewPassword()), await api.AddAccountAsync(NewCode(), NewPassword()));

        using var scope = hooked.Services.CreateScope();
        await NotifyWithAZoneChangeAsync(scope, zone.Id, owner.Id, otherOwner.Id);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        Assert.Equal([2, 2], seen);
    }

    [SqlServerFact]
    public async Task The_recipient_query_on_sql_server_follows_the_status_the_phone_and_the_pairs()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (zone, other) = (await SeedZoneAsync(database), await SeedZoneAsync(database));
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());
        var (_, client) = await ManagerAsync(api, zone.Id);
        var twoPairs = await ManagerAccountAsync(api, zone.Id);
        await api.GrantAsync(twoPairs, StaffRole.Accountant, zone.Id);
        var accountantHereManagerElsewhere = await ManagerAccountAsync(api, other.Id);
        await api.GrantAsync(accountantHereManagerElsewhere, StaffRole.Accountant, zone.Id);
        var locked = await ManagerAccountAsync(api, zone.Id);
        await SetAccountAsync(database, locked.Id, a => a.StatusId = StatusIds.Account.Locked);
        var unverified = await ManagerAccountAsync(api, zone.Id);
        await SetAccountAsync(database, unverified.Id, a => a.PhoneVerifiedAt = null);

        var response = await client.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements", Measurement());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(api.SmsTo(owner));
        Assert.Single(api.SmsTo(twoPairs));
        Assert.Empty(api.SmsTo(accountantHereManagerElsewhere));
        Assert.Empty(api.SmsTo(locked));
        Assert.Empty(api.SmsTo(unverified));
    }

    // Accounts cannot share a number here, so "one number, two SMS" cannot happen through this rule.
    [SqlServerFact]
    public async Task Two_accounts_cannot_hold_the_same_number()
    {
        await using var database = SqlServerTestDatabase.Create();
        await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var (first, second) = (await api.AddAccountAsync(NewCode(), NewPassword()), await api.AddAccountAsync(NewCode(), NewPassword()));

        await Assert.ThrowsAsync<DbUpdateException>(() => SetAccountAsync(database, second.Id, a => a.PhoneNumber = first.PhoneNumber));
    }

    // ---- Helpers ----

    /// <summary>Rows of two tables, so EF really opens a transaction around the save (one multi-row INSERT would not need one).</summary>
    private static async Task NotifyWithAZoneChangeAsync(IServiceScope scope, Guid zoneId, params Guid[] recipients)
    {
        (await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().FarmingZones.SingleAsync(z => z.Id == zoneId)).Name = "Changed";
        await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync(recipients, Withdrawn(), CancellationToken.None);
    }

    /// <summary>Notify and change a zone; another connection changes the zone first, so the save loses; returns the tracked zone.</summary>
    private static async Task<FarmingZone> ConflictedSaveAsync(SqlServerTestDatabase database, IServiceScope scope, Guid zoneId, Guid ownerId)
    {
        var context = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var tracked = await context.FarmingZones.SingleAsync(z => z.Id == zoneId);
        tracked.Name = "Changed here";
        await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync([ownerId], Withdrawn(), CancellationToken.None);
        await using (var other = database.CreateContext())
        {
            (await other.FarmingZones.SingleAsync(z => z.Id == zoneId)).Name = "Changed there";
            await other.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
        return tracked;
    }

    /// <summary>
    /// A real deadlock with the save as its victim: another session holds the second account, the save takes the first and
    /// waits for the second, then the session asks for the first with the higher deadlock priority.
    /// </summary>
    private static async Task<Exception> DeadlockedSaveAsync(SqlServerTestDatabase database, IServiceScope scope, Guid ownerId, Guid firstId, Guid secondId)
    {
        var context = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var table = context.Model.FindEntityType(typeof(Account))!.GetTableName();
        (await context.Accounts.SingleAsync(a => a.Id == firstId)).DisplayName = "Save 1";
        (await context.Accounts.SingleAsync(a => a.Id == secondId)).DisplayName = "Save 2";
        await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync([ownerId], Withdrawn(), CancellationToken.None);

        await using var session = new SqlConnection(database.ConnectionString);
        await session.OpenAsync();
        await using var transaction = (SqlTransaction)await session.BeginTransactionAsync();
        await ExecuteAsync(session, transaction, $"SET DEADLOCK_PRIORITY HIGH; UPDATE [{table}] SET DisplayName = DisplayName WHERE Id = '{secondId}';");

        var save = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        // Long enough for the save to hold the first row and wait on the second; a slower save only makes the session wait in turn.
        await Task.Delay(1500);
        var sessionUpdate = ExecuteAsync(session, transaction, $"UPDATE [{table}] SET DisplayName = DisplayName WHERE Id = '{firstId}';");

        // EF wraps error 1205 as a transient failure: an InvalidOperationException around the DbUpdateException.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => save);
        await sessionUpdate;
        await transaction.RollbackAsync();
        return error;
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Two accounts in the order EF writes them (by key).</summary>
    private static async Task<(Guid First, Guid Second)> TwoAccountsInSaveOrderAsync(SqlServerApiFactory api)
    {
        var (a, b) = (await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff), await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff));
        return a.Id.CompareTo(b.Id) < 0 ? (a.Id, b.Id) : (b.Id, a.Id);
    }

    private static async Task SetAccountAsync(SqlServerTestDatabase database, Guid id, Action<Account> change)
    {
        await using var db = database.CreateContext();
        change(await db.Accounts.SingleAsync(a => a.Id == id));
        await db.SaveChangesAsync();
    }

    private static WebApplicationFactory<Program> Hooked(SqlServerApiFactory api, SqlServerTestDatabase database, CommitHook hook) =>
        api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<OmtsDbContext>>();
            services.AddDbContext<OmtsDbContext>((provider, options) => options
                .UseSqlServer(database.ConnectionString)
                .AddOmtsInterceptors(provider)
                .AddInterceptors(hook));
        }));

    private static async Task<Account> ManagerAccountAsync(SqlServerApiFactory api, Guid zoneId, string? password = null)
    {
        var account = await api.AddAccountAsync(NewCode(), password ?? NewPassword(), SubjectType.Staff);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var row = await db.Accounts.SingleAsync(a => a.Id == account.Id);
        row.PhoneNumber = NewPhone();
        row.PhoneVerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await api.GrantAsync(row, StaffRole.ZoneManager, zoneId);
        return row;
    }

    private static async Task<(Account Account, HttpClient Client)> ManagerAsync(SqlServerApiFactory api, Guid zoneId)
    {
        var password = NewPassword();
        var account = await ManagerAccountAsync(api, zoneId, password);
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, account.Username, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (account, client);
    }

    private static async Task<FarmingZone> SeedZoneAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var cooperative = await db.Cooperatives.FirstOrDefaultAsync() ?? new Cooperative { FullName = "HTX Trung Nam" };
        var zone = new FarmingZone { Cooperative = cooperative, Code = NewCode(), Name = "Vùng SQL", PhMin = 6.5m, PhMax = 8.5m };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        return zone;
    }

    /// <summary>Runs an action at the moment around the real commit of a save.</summary>
    private sealed class CommitHook : DbTransactionInterceptor
    {
        public Action? BeforeCommit { get; set; }

        public Action? AfterCommit { get; set; }

        public bool CommitReached { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            CommitReached = true;
            BeforeCommit?.Invoke();
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            AfterCommit?.Invoke();
            return Task.CompletedTask;
        }
    }
}
