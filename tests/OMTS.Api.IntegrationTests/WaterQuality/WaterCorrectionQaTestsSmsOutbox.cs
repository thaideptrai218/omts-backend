using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// QA of <c>MandatorySmsOutboxInterceptor</c> as any module sees it, one request scope on a real SQL Server: mandatory SMS go out
/// after a success, not after a failure, and a later success in the same scope is not affected.
/// </summary>
public class WaterCorrectionQaTestsSmsOutbox
{
    [SqlServerFact]
    public async Task An_sms_goes_out_after_the_save_commits_and_never_twice()
    {
        await using var test = await Scoped.StartAsync();

        test.Outbox.Enqueue(test.Sms("first"));
        Assert.Empty(test.Inbox());
        await test.UnitOfWork.SaveChangesAsync();
        await test.UnitOfWork.SaveChangesAsync();

        Assert.Equal(["first"], test.Inbox());
    }

    [SqlServerFact]
    public async Task A_conflict_sends_no_sms_and_after_a_discard_only_the_new_one_goes_out()
    {
        await using var test = await Scoped.StartAsync();
        await test.LoseNextSaveAsync();
        test.Outbox.Enqueue(test.Sms("lost"));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => test.UnitOfWork.SaveChangesAsync());
        Assert.Empty(test.Inbox());

        test.UnitOfWork.DiscardChanges();
        test.Outbox.Enqueue(test.Sms("rerun"));
        await test.UnitOfWork.SaveChangesAsync();

        Assert.Equal(["rerun"], test.Inbox());
    }

    [SqlServerFact]
    public async Task A_success_keeps_its_sms_when_a_later_save_of_the_request_conflicts_or_fails()
    {
        await using var test = await Scoped.StartAsync();
        test.Outbox.Enqueue(test.Sms("saved"));
        await test.UnitOfWork.SaveChangesAsync();

        await test.LoseNextSaveAsync();
        test.Outbox.Enqueue(test.Sms("conflict"));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => test.UnitOfWork.SaveChangesAsync());
        test.UnitOfWork.DiscardChanges();

        test.Db.Rafts.Add(new Raft { ZoneId = test.Zone.Id, Code = test.RaftCode });
        test.Outbox.Enqueue(test.Sms("duplicate"));
        await Assert.ThrowsAsync<DuplicateRecordException>(() => test.UnitOfWork.SaveChangesAsync());
        test.UnitOfWork.DiscardChanges();

        test.Outbox.Enqueue(test.Sms("last"));
        await test.UnitOfWork.SaveChangesAsync();

        Assert.Equal(["last", "saved"], test.Inbox().Order(StringComparer.Ordinal));
    }

    // IUnitOfWork.ReloadAsync is documented for "retry after a conflict" and AuthService, CurrentPasswordProof and
    // VerificationService retry that way. A module that notifies and retries so keeps its Notification row (it stays Added),
    // so its SMS must stay too: BR-ACCOUNT-11 mandatory types also go by SMS.
    [SqlServerFact]
    public async Task A_notification_saved_by_a_reload_retry_still_sends_its_sms()
    {
        await using var test = await Scoped.StartAsync();
        var zone = await test.LoseNextSaveAsync();
        await test.Dispatcher.NotifyAsync(
            [test.Account.Id],
            new NotificationContent(NotificationTypes.WaterAlert, "Title", new UserMessage("MSG-QA", "kept row")),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => test.UnitOfWork.SaveChangesAsync());
        await test.UnitOfWork.ReloadAsync(zone);
        zone.Name = "Reloaded";
        await test.UnitOfWork.SaveChangesAsync();

        Assert.Equal(1, await test.Db.Notifications.AsNoTracking().CountAsync(n => n.RecipientAccountId == test.Account.Id));
        Assert.Equal(["kept row"], test.Inbox());
    }

    private sealed class Scoped(SqlServerTestDatabase database, SqlServerApiFactory api, IServiceScope scope, FarmingZone zone, string raftCode, Account account) : IAsyncDisposable
    {
        public FarmingZone Zone => zone;

        public string RaftCode => raftCode;

        public Account Account => account;

        public OmtsDbContext Db => scope.ServiceProvider.GetRequiredService<OmtsDbContext>();

        public IUnitOfWork UnitOfWork => scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        public IMandatorySmsOutbox Outbox => scope.ServiceProvider.GetRequiredService<IMandatorySmsOutbox>();

        public INotificationDispatcher Dispatcher => scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();

        public static async Task<Scoped> StartAsync()
        {
            var database = SqlServerTestDatabase.Create();
            var seeded = await WaterSqlServerKit.SeedAsync(database);
            var api = new SqlServerApiFactory(database);
            var account = await api.AddAccountAsync(NewCode(), NewPassword());
            return new Scoped(database, api, api.Services.CreateScope(), seeded.Zone, seeded.Raft.Code, account);
        }

        public MandatorySmsMessage Sms(string body) => new(account.Id, "QA", account.PhoneNumber, body);

        public IReadOnlyList<string> Inbox() =>
            api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(account.PhoneNumber!).Select(m => m.Text).Order(StringComparer.Ordinal).ToList();

        /// <summary>Reads the zone in this scope, then changes its row from elsewhere: the next save of a change to it conflicts.</summary>
        public async Task<FarmingZone> LoseNextSaveAsync()
        {
            var tracked = await Db.FarmingZones.SingleAsync(z => z.Id == zone.Id);
            tracked.Name = "This request";
            await using var elsewhere = database.CreateContext();
            var row = await elsewhere.FarmingZones.SingleAsync(z => z.Id == zone.Id);
            row.Name = $"Elsewhere {NewCode()}";
            await elsewhere.SaveChangesAsync();
            return tracked;
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await api.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
