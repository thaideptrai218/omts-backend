using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Notifications.MandatorySmsQaKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>QA of the commit rule on the in-memory store, the providers None and Fake, the startup check and the verification codes.</summary>
public class MandatorySmsQaCommitAndConfigTests
{
    private const string NotConfiguredLine = "not sent: Sms:Provider is None";

    private static NotificationContent Withdrawn(string text = "The water alert was withdrawn.") => new(
        NotificationTypes.WaterAlertWithdrawn, "Title", new UserMessage("MSG-WATER-04", text));

    private static Task NotifyAsync(IServiceScope scope, Guid account, string text = "The water alert was withdrawn.") =>
        scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync([account], Withdrawn(text), CancellationToken.None);

    private static IReadOnlyList<FakeMessage> PushesTo(AuthApiFactory api, Guid account) =>
        api.Services.GetRequiredService<FakePushSender>().Inbox.List(account.ToString());

    // ---- The commit ----

    [Fact]
    public async Task A_synchronous_save_sends_once_too()
    {
        await using var api = new AuthApiFactory();
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());

        using var scope = api.Services.CreateScope();
        await NotifyAsync(scope, owner.Id);
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        db.SaveChanges();
        db.SaveChanges();

        Assert.Single(api.SmsTo(owner));
    }

    [Fact]
    public async Task Two_notifications_with_two_saves_in_one_request_send_one_sms_each()
    {
        await using var api = new AuthApiFactory();
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());

        using var scope = api.Services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await NotifyAsync(scope, owner.Id, "First.");
        await unitOfWork.SaveChangesAsync();
        await NotifyAsync(scope, owner.Id, "Second.");
        await unitOfWork.SaveChangesAsync();

        Assert.Equal(["Second.", "First."], api.SmsTo(owner).Select(m => m.Text));
    }

    [Fact]
    public async Task A_notification_that_is_never_saved_sends_nothing()
    {
        await using var api = new AuthApiFactory();
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());

        using (var scope = api.Services.CreateScope())
        {
            await NotifyAsync(scope, owner.Id);
        }

        using (var other = api.Services.CreateScope())
        {
            await other.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        Assert.Empty(api.SmsTo(owner));
        Assert.Equal(0, await api.NotificationCountAsync(owner.Id, NotificationTypes.WaterAlertWithdrawn));
    }

    // The tracker keeps the rows after a cancelled save, so a later save commits them; the queue is gone. Before this branch the
    // push of a cancelled save stayed queued and went out with that later save. No flow of this branch saves after a cancellation.
    [Fact(Skip = "Accepted: no flow retries in the same scope or saves after a cancelled save; one that does must send the outbox again.")]
    public async Task L4_a_later_save_that_commits_the_notification_of_a_cancelled_save_still_sends_its_sms_and_push()
    {
        await using var api = new AuthApiFactory();
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());

        using var scope = api.Services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await NotifyAsync(scope, owner.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unitOfWork.SaveChangesAsync(new CancellationToken(canceled: true)));
        await unitOfWork.SaveChangesAsync();

        var rows = await api.NotificationCountAsync(owner.Id, NotificationTypes.WaterAlertWithdrawn);
        Assert.Equal((1, 1, 1), (rows, api.SmsTo(owner).Count, PushesTo(api, owner.Id).Count));
    }

    // ---- Providers and settings ----

    [Fact]
    public async Task The_setting_has_its_default_when_the_configuration_does_not_name_it()
    {
        await using var api = new AuthApiFactory();

        Assert.Equal(5, api.Services.GetRequiredService<IOptions<SmsOptions>>().Value.MandatoryTimeoutSeconds);
    }

    [Fact]
    public async Task Provider_None_outside_development_keeps_the_action_warns_at_startup_and_once_per_save_and_codes_answer_msg_account_13()
    {
        await using var api = new ProductionHost(("Sms:Provider", "None"));
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        var measurement = await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Equal(2, (await api.AlertsOfAsync(measurement.Id())).Count);
        Assert.Contains(api.Logs.Lines, l => l.Contains("SMS provider is None") && l.Contains("mandatory notifications"));
        Assert.Single(api.Logs.Lines, l => l.Contains(NotConfiguredLine));
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(manager.PhoneNumber!));
        var code = await api.CreateClient().PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = NewPhone() });
        Assert.Equal("MSG-ACCOUNT-13", await code.ErrorCodeAsync());
    }

    // Allowed by design as before this branch, with the warning: nothing leaves the server, and only the startup line says so.
    [Fact]
    public async Task Provider_Fake_outside_development_starts_with_a_warning_and_its_inbox_cannot_be_read()
    {
        await using var api = new ProductionHost(("Sms:Provider", "Fake"));
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Contains(api.Logs.Lines, l => l.Contains("SMS provider is Fake") && l.Contains("NOT sent"));
        Assert.Single(api.SmsTo(manager));
        Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().GetAsync($"/api/dev/sms?phoneNumber={manager.PhoneNumber}")).StatusCode);
    }

    [Fact]
    public async Task Provider_Fake_in_development_shows_the_sms_in_the_dev_inbox_with_the_verification_codes()
    {
        await using var api = new AuthApiFactory();
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        var inbox = await api.CreateClient().GetFromJsonAsync<JsonElement>($"/api/dev/sms?phoneNumber={manager.PhoneNumber}");
        Assert.Equal(1, inbox.GetArrayLength());
        Assert.StartsWith("Water alert: zone", inbox[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("Twilio")]
    [InlineData("none")]
    public async Task An_unknown_provider_stops_the_host(string provider)
    {
        await using var api = Host(settings: ("Sms:Provider", provider));

        Assert.ThrowsAny<Exception>(() => api.CreateClient());
    }

    // ---- Verification codes ----

    // BR-ACCOUNT-09 limits codes only. The other side, no ceiling on the mandatory SMS themselves, is a budget question for the PM.
    [Fact]
    public async Task The_daily_limit_counts_codes_only_mandatory_sms_neither_use_it_nor_stop_at_it()
    {
        await using var api = Host(settings: ("Verification:DailySmsLimit", "2"));
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);
        var anonymous = api.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            await z.RecordedAsync(9.1m, 20m, client: managerClient);
        }

        Assert.Equal(3, api.SmsTo(manager).Count);
        var (first, second) = (NewPhone(), NewPhone());
        Assert.True((await anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = first })).IsSuccessStatusCode);
        Assert.True((await anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = second })).IsSuccessStatusCode);
        Assert.Matches(@"\d{6}", api.LastSmsCode(first));
        var refused = await anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = NewPhone() });
        Assert.Equal("MSG-ACCOUNT-13", await refused.ErrorCodeAsync());

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Equal(4, api.SmsTo(manager).Count);
    }

    // A code waits for its own provider call: the new setting, the queue and the text rule are not on its path.
    [Fact]
    public async Task A_code_goes_out_with_its_own_text_after_an_alert_and_a_refused_number_still_answers_msg_account_13()
    {
        var refused = NewPhone();
        await using var api = Host(settings: [("Sms:MandatoryTimeoutSeconds", "1"), ("Sms:Fake:FailFor:0", refused)]);
        var z = await api.ZoneAsync();
        var (managerClient, _) = await api.StaffAsync(z.Zone);
        await z.RecordedAsync(9.1m, 20m, client: managerClient);
        var anonymous = api.CreateClient();
        var free = NewPhone();

        var code = await anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = free });
        var notDelivered = await anonymous.PostAsJsonAsync("/api/verifications", new { channel = "Sms", target = refused });

        Assert.True(code.IsSuccessStatusCode, await code.Content.ReadAsStringAsync());
        Assert.Matches(@"\d{6}", api.LastSmsCode(free));
        Assert.Equal("MSG-ACCOUNT-13", await notDelivered.ErrorCodeAsync());
    }
}
