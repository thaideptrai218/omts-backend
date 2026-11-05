using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA of OMTS-223: DemoPurchaseRequestStep answers through the real services, so the seed hands two SMS to the Fake or None. What that does with
/// those, and that any other sender gets nothing.
/// </summary>
public class PurchaseRequestQaDemoSqlServerTests
{
    private sealed class RecordingSender(Func<DeliveryResult> result) : ITraderMessageSender, ISmsSender
    {
        public List<(string Phone, string Text)> Sent { get; } = [];

        public NotificationChannel Channel => NotificationChannel.Sms;

        public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add((phoneNumber, text));
            }

            return Task.FromResult(result());
        }
    }

    private sealed record Host(SqlServerTestDatabase Database, SqlServerApiFactory Api, string StorageRoot) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            await Database.DisposeAsync();
            if (Directory.Exists(StorageRoot))
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
        }
    }

    private static Host Start(string? smsProvider = null)
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        var settings = new Dictionary<string, string?>
        {
            [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã Nuôi hàu Vân Đồn",
            [BootstrapSettings.OwnerDisplayNameKey] = "Nguyễn Văn Hải",
            [BootstrapSettings.OwnerUsernameKey] = "owner",
            [BootstrapSettings.OwnerPhoneNumberKey] = NewPhone(),
            [BootstrapSettings.OwnerTemporaryPasswordKey] = NewPassword(),
            ["Storage:Local:RootPath"] = storageRoot,
        };
        if (smsProvider is not null)
        {
            settings["Sms:Provider"] = smsProvider;
        }

        var database = SqlServerTestDatabase.Create();
        return new Host(database, new SqlServerApiFactory(database, settings), storageRoot);
    }

    private static async Task<BootstrapOutcome> SeedAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(seedDemoData: true, CancellationToken.None);
    }

    private static Task<List<OMTS.Domain.Entities.Platform.Notification>> SendingsAsync(WebApplicationFactory<Program> api) =>
        api.DbAsync(db => db.Notifications.AsNoTracking().Where(n => n.Channel == NotificationChannel.Sms).ToListAsync());

    private static Task<(int Requests, int Notifications, int Audit)> CountsAsync(WebApplicationFactory<Program> api) =>
        api.DbAsync(async db => (await db.PurchaseRequests.CountAsync(), await db.Notifications.CountAsync(), await db.AuditEvents.CountAsync()));

    [SqlServerFact]
    public async Task With_the_fake_two_messages_are_sent_to_the_demo_number_and_a_rerun_sends_none()
    {
        await using var host = Start();

        Assert.Equal(DeploymentBootstrapper.Success, (await SeedAsync(host.Api)).ExitCode);

        var rows = await SendingsAsync(host.Api);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(StatusIds.MessageDelivery.Sent, r.DeliveryStatusId));
        var phone = rows.Select(r => r.PhoneNumber!).Distinct().Single();
        var inbox = host.Api.Services.GetRequiredService<FakeSmsSender>().Inbox;
        var before = await CountsAsync(host.Api);
        var delivered = inbox.List(phone).Count;

        Assert.Equal(DeploymentBootstrapper.Success, (await SeedAsync(host.Api)).ExitCode);

        Assert.Equal(before, await CountsAsync(host.Api));
        Assert.Equal(delivered, inbox.List(phone).Count);

        // The client list, §10: the demo trader's four, and what the Owner has in the list.
        var (ownerList, states, responders) = await host.Api.DbAsync(async db =>
        {
            var owner = await db.Accounts.AsNoTracking().SingleAsync(a => a.Username == "owner");
            return (
                await db.Notifications.AsNoTracking().CountAsync(n => n.RecipientAccountId == owner.Id && n.Channel == NotificationChannel.InApp && n.RelatedRecordType == RelatedRecordTypes.PurchaseRequest),
                await db.PurchaseRequests.AsNoTracking().Select(r => r.StatusId).OrderBy(s => s).ToListAsync(),
                await db.PurchaseRequests.AsNoTracking().Where(r => r.RespondedAt != null).Select(r => r.RespondedByAccountId).Distinct().ToListAsync());
        });
        Assert.Equal(5, ownerList);
        Assert.Equal([3201, 3202, 3203, 3204], states);
        Assert.Single(responders);
        Assert.NotNull(responders[0]);
    }

    [SqlServerFact]
    public async Task With_none_the_seed_runs_and_both_rows_say_not_sent_and_a_rerun_adds_nothing()
    {
        await using var host = Start("None");

        var outcome = await SeedAsync(host.Api);

        Assert.True(outcome.ExitCode == DeploymentBootstrapper.Success, string.Join(" | ", outcome.Messages));
        var rows = await SendingsAsync(host.Api);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((StatusIds.MessageDelivery.NotSent, "NOT_CONFIGURED"), (r.DeliveryStatusId, r.ProviderErrorCode)));
        var before = await CountsAsync(host.Api);

        Assert.Equal(DeploymentBootstrapper.Success, (await SeedAsync(host.Api)).ExitCode);

        Assert.Equal(before, await CountsAsync(host.Api));
    }

    [SqlServerFact]
    public async Task A_sender_that_refuses_does_not_stop_the_seed()
    {
        await using var host = Start();
        var sender = new RecordingSender(() => DeliveryResult.Failed("REFUSED"));
        await using var hooked = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ITraderMessageSender>(sender)));

        var outcome = await SeedAsync(hooked);

        Assert.True(outcome.ExitCode == DeploymentBootstrapper.Success, string.Join(" | ", outcome.Messages));
        // L4: not the Fake or None, so it is never asked. Its refusal cannot reach the seed, and no row says Not sent.
        Assert.Empty(sender.Sent);
        Assert.Empty(await SendingsAsync(hooked));
    }

    // Pinned, an observation: the first request is the mark that the step is done, so a seed that dies after it is not completed by a rerun.
    [SqlServerFact]
    public async Task A_seed_that_fails_after_the_first_request_is_not_completed_by_a_rerun()
    {
        await using var host = Start();
        await using var hooked = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<OMTS.Application.Common.Interfaces.Persistence.ICooperativeOwnerQuery>(_ => new FailsOnSecondCall())));

        await Assert.ThrowsAnyAsync<Exception>(() => SeedAsync(hooked));
        Assert.Equal(1, await host.Api.DbAsync(db => db.PurchaseRequests.CountAsync()));

        Assert.Equal(DeploymentBootstrapper.Success, (await SeedAsync(host.Api)).ExitCode);

        Assert.Equal(1, await host.Api.DbAsync(db => db.PurchaseRequests.CountAsync()));
    }

    private sealed class FailsOnSecondCall : OMTS.Application.Common.Interfaces.Persistence.ICooperativeOwnerQuery
    {
        private static int _calls;

        public Task<IReadOnlyList<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) >= 2 ? throw new InvalidOperationException("The database went away.") : Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    // L4 by the way a real provider will come: a new ISmsSender under Sms:Provider. The requests are seeded unanswered, the output says so once, and a rerun adds nothing.
    [SqlServerFact]
    public async Task With_a_real_sms_provider_the_requests_stay_unanswered_and_the_seed_says_so()
    {
        await using var host = Start();
        var sms = new RecordingSender(() => DeliveryResult.Sent("real-1"));
        await using var hooked = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ISmsSender>(sms)));

        var outcome = await SeedAsync(hooked);

        Assert.True(outcome.ExitCode == DeploymentBootstrapper.Success, string.Join(" | ", outcome.Messages));
        Assert.Empty(sms.Sent);
        Assert.Empty(await SendingsAsync(hooked));
        Assert.Equal([3201, 3201, 3201, 3204], await hooked.DbAsync(db => db.PurchaseRequests.AsNoTracking().Select(r => r.StatusId).OrderBy(s => s).ToListAsync()));
        Assert.Single(outcome.Messages, m => m.Contains("neither Fake nor None"));

        var rerun = await SeedAsync(hooked);

        Assert.DoesNotContain(rerun.Messages, m => m.Contains("neither Fake nor None"));
        Assert.Equal(4, await hooked.DbAsync(db => db.PurchaseRequests.CountAsync()));
    }

    // The step's own promise, now kept: nothing but the Fake or None is handed a message, whatever Sms:Provider says.
    [SqlServerFact]
    public async Task L4_the_demo_seed_hands_no_message_to_a_sender_that_is_not_the_fake()
    {
        await using var host = Start();
        var sender = new RecordingSender(() => DeliveryResult.Sent("real-1"));
        await using var hooked = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ITraderMessageSender>(sender)));

        Assert.Equal(DeploymentBootstrapper.Success, (await SeedAsync(hooked)).ExitCode);

        Assert.True(sender.Sent.Count == 0, $"Handed over {sender.Sent.Count}: {string.Join(" | ", sender.Sent.Select(m => m.Phone + ": " + m.Text))}");
    }
}
