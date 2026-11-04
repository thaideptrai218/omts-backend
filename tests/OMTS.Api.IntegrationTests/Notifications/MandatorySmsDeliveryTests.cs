using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Notifications.MandatorySmsKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>GFR-06 and the commit rule: the SMS never touches the business action, and goes out once, only after the commit.</summary>
public class MandatorySmsDeliveryTests
{
    private const string NotConfiguredLine = "not sent: Sms:Provider is None";

    [Fact]
    public async Task A_provider_that_throws_keeps_the_action_the_rows_and_the_push_and_tells_the_logs_nothing_personal()
    {
        await using var api = WithSender(new ThrowingSmsSender());
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        var measurement = await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Equal(2, (await api.AlertsOfAsync(measurement.Id())).Count);
        Assert.NotEmpty(api.Services.GetRequiredService<FakePushSender>().Inbox.List(manager.Id.ToString()));
        var failure = Assert.Single(api.Logs.Lines, l => l.Contains($"Mandatory SMS WATER_ALERT to account {manager.Id} failed: HttpRequestException"));
        Assert.DoesNotContain(manager.PhoneNumber!, failure);
        AssertNoSmsDetailInTheLogs(api, manager.PhoneNumber!, z.OwnerAccount.PhoneNumber!, z.Zone.Name);
    }

    [Fact]
    public async Task A_refused_number_keeps_the_action_and_the_others_still_get_theirs()
    {
        var refused = NewPhone();
        await using var api = WithSetting("Sms:Fake:FailFor:0", refused);
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);
        await api.UpdateAsync<Domain.Entities.Identity.Account>(manager.Id, a => a.PhoneNumber = refused);

        var measurement = await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Equal(2, (await api.AlertsOfAsync(measurement.Id())).Count);
        Assert.Single(api.SmsTo(z.OwnerAccount));
        Assert.Contains(api.Logs.Lines, l => l.Contains($"Mandatory SMS WATER_ALERT to account {manager.Id} failed: FAKE_CONFIGURED_FAILURE"));
    }

    // The provider never answers and ignores its cancellation: the answer waits for the timeout, not for the provider.
    [Fact]
    public async Task A_provider_that_never_answers_holds_the_answer_for_the_timeout_only()
    {
        var hanging = new HangingSmsSender();
        await using var api = new WaterApiFactory(
            new Dictionary<string, string> { ["Sms:MandatoryTimeoutSeconds"] = "1" },
            services => services.AddSingleton<OMTS.Application.Common.Interfaces.Messaging.ISmsSender>(hanging));
        var z = await api.ZoneAsync();
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        var clock = Stopwatch.StartNew();
        var measurement = await z.RecordedAsync(9.1m, 20m, client: managerClient);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The answer took {clock.Elapsed}.");
        Assert.Equal(2, hanging.Calls);
        Assert.Equal(2, (await api.AlertsOfAsync(measurement.Id())).Count);
        Assert.Contains(api.Logs.Lines, l => l.Contains("2 mandatory SMS had no answer after 1 seconds"));
    }

    [Fact]
    public async Task An_account_without_a_phone_or_with_an_unverified_one_gets_no_sms_and_one_log_line_and_nothing_fails()
    {
        await using var api = new AuthApiFactory();
        var z = await api.ZoneAsync();
        var noPhone = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.CooperativeOwner, a => a.PhoneNumber = null);
        var unverified = await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.CooperativeOwner, a => a.PhoneVerifiedAt = null);
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Empty(api.SmsTo(unverified));
        Assert.Single(api.SmsTo(z.OwnerAccount));
        Assert.Single(api.SmsTo(manager));
        Assert.Equal(1, await api.NotificationCountAsync(noPhone.Id, NotificationTypes.WaterAlert));
        foreach (var account in new[] { noPhone, unverified })
        {
            Assert.Single(api.Logs.Lines, l => l.Contains($"Mandatory SMS WATER_ALERT to account {account.Id} not sent: the account has no verified phone number."));
        }

        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(unverified.PhoneNumber!));
    }

    // One clear line for the save, also with several recipients and one who has no phone; the sender is not asked at all.
    [Fact]
    public async Task Provider_None_sends_nothing_fails_nothing_and_writes_one_line()
    {
        await using var api = WithSetting("Sms:Provider", "None");
        var z = await api.ZoneAsync();
        await api.AddAccountAsync(NewCode(), NewPassword(), SubjectType.CooperativeOwner, a => a.PhoneNumber = null);
        var (managerClient, _) = await api.StaffAsync(z.Zone);
        var linesBefore = api.Logs.Lines.Count(l => l.Contains(NotConfiguredLine));

        var measurement = await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Equal(3, (await api.AlertsOfAsync(measurement.Id())).Count);
        Assert.Empty(api.AllSms());
        Assert.Equal(linesBefore + 1, api.Logs.Lines.Count(l => l.Contains(NotConfiguredLine)));
        Assert.Contains(api.Logs.Lines, l => l.Contains($"3 mandatory SMS of WATER_ALERT {NotConfiguredLine}"));
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains("no verified phone number") || l.Contains("no SMS provider is configured"));
    }

    [Fact]
    public async Task A_second_save_in_the_same_request_sends_nothing_again()
    {
        await using var api = new AuthApiFactory();
        var z = await api.ZoneAsync();

        using var scope = api.Services.CreateScope();
        await NotifyOwnerAsync(scope.ServiceProvider, z);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.SaveChangesAsync();
        await unitOfWork.SaveChangesAsync();

        Assert.Single(api.SmsTo(z.OwnerAccount));
    }

    // SQL Server shows a real failed commit (MandatorySmsSqlServerTests); this is a save cancelled before it reaches the store.
    [Fact]
    public async Task A_cancelled_save_drops_the_sms_and_a_later_save_of_the_request_does_not_send_it()
    {
        await using var api = new AuthApiFactory();
        var z = await api.ZoneAsync();

        using var scope = api.Services.CreateScope();
        await NotifyOwnerAsync(scope.ServiceProvider, z);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unitOfWork.SaveChangesAsync(new CancellationToken(canceled: true)));
        Assert.Empty(api.SmsTo(z.OwnerAccount));

        await unitOfWork.SaveChangesAsync();

        Assert.Empty(api.SmsTo(z.OwnerAccount));
    }

    // Nothing queued means nothing leaves: the rows of a notification are not the only thing a host saves.
    [Fact]
    public async Task A_save_without_a_notification_sends_nothing()
    {
        var counting = new CountingSmsSender();
        await using var api = WithSender(counting);
        var z = await api.ZoneAsync();

        var response = await z.RecordAsync(7.5m, 20m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, counting.Calls);
    }

    private static Task NotifyOwnerAsync(IServiceProvider services, WaterZone z) =>
        services.GetRequiredService<INotificationDispatcher>().NotifyAsync(
            [z.OwnerAccount.Id],
            new NotificationContent(
                NotificationTypes.WaterAlertWithdrawn, "Water alert withdrawn", new UserMessage("MSG-WATER-04", "The water alert was withdrawn.")),
            CancellationToken.None);

    private static void AssertNoSmsDetailInTheLogs(AuthApiFactory api, params string[] details)
    {
        foreach (var detail in details)
        {
            Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(detail, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains("Please check the water", StringComparison.Ordinal));
    }
}
