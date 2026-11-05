using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using FarmingKit = OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using LicenseFields = OMTS.Api.IntegrationTests.Farming.LicenseFields;
using static OMTS.Api.IntegrationTests.Notifications.MandatorySmsKit;
using static OMTS.Api.IntegrationTests.Notifications.MandatorySmsQaKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>QA of GFR-06 and <c>Sms:MandatoryTimeoutSeconds</c>: what a bad SMS provider can and cannot do to a business action.</summary>
public class MandatorySmsQaFailureTests
{
    private const string Timeout = "Sms:MandatoryTimeoutSeconds";

    // Far above the 1-second limit of these tests and far below the 60 seconds a provider would hold: a loaded machine does not fail them.
    private static readonly TimeSpan WellWithinTheHold = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Each_way_of_failing_stays_with_its_own_number_and_the_answer_stays_right()
    {
        var modes = new Dictionary<string, Func<Task<DeliveryResult>>>
        {
            ["sync"] = () => throw new InvalidOperationException("thrown at once"),
            ["async"] = () => Task.FromException<DeliveryResult>(new HttpRequestException("faulted")),
            ["cancelled"] = () => Task.FromCanceled<DeliveryResult>(new CancellationToken(true)),
            ["refused"] = () => Task.FromResult(DeliveryResult.Failed("REFUSED")),
            ["null"] = () => Task.FromResult<DeliveryResult>(null!),
        };
        var byNumber = new Dictionary<string, Func<Task<DeliveryResult>>>();
        var sender = new RecordingSmsSender(number => byNumber.TryGetValue(number, out var mode) ? mode() : null);
        await using var api = Host(sender);
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);
        foreach (var mode in modes.Values)
        {
            var (_, failing) = await api.StaffAsync(z.Zone);
            byNumber[failing.PhoneNumber!] = mode;
        }

        var measurement = await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Equal(7, (await api.AlertsOfAsync(measurement.Id())).Count);
        Assert.Equal(new[] { manager.PhoneNumber!, z.OwnerAccount.PhoneNumber! }.Order(), sender.Numbers.Order());
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync($"/api/water-measurements/{measurement.Id()}")).StatusCode);
        Assert.Equal(5, api.Logs.Lines.Count(l => l.Contains("Mandatory SMS WATER_ALERT to account") && l.Contains("failed:")));
    }

    // GFR-06, client list: "same status, same body". The two answers differ in ids and times only.
    [Fact]
    public async Task A_failed_sms_gives_the_same_answer_as_a_delivered_one_and_a_re_send_does_not_retry_it()
    {
        var failing = false;
        var sender = new RecordingSmsSender(_ => failing ? throw new HttpRequestException("provider down") : null);
        await using var api = Host(sender);
        var z = await api.ZoneAsync();
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        var delivered = await z.RecordedAsync(9.1m, 20m, client: managerClient);
        var sentBefore = sender.Numbers.Count;
        failing = true;
        var operation = Guid.NewGuid();
        var lost = await z.RecordAsync(9.1m, 20m, clientOperationId: operation, client: managerClient);
        var body = await lost.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        failing = false;
        var again = await z.RecordAsync(9.1m, 20m, clientOperationId: operation, client: managerClient);

        Assert.Equal(HttpStatusCode.Created, lost.StatusCode);
        Assert.Equal(delivered.EnumerateObject().Select(p => p.Name), body.EnumerateObject().Select(p => p.Name));
        foreach (var field in new[] { "status", "ph", "salinity" })
        {
            Assert.Equal(delivered.GetProperty(field).ToString(), body.GetProperty(field).ToString());
        }

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(sentBefore, sender.Numbers.Count);
    }

    // Each license has its own scope and save: one bad provider call neither stops the job nor sends the notice twice on the next run.
    [Fact]
    public async Task A_license_job_goes_through_a_provider_that_hangs_and_keeps_its_marker()
    {
        await using var api = Host(new HangingSmsSender(), settings: (Timeout, "1"));
        var owner = await FarmingKit.OwnerAsync(api);
        var zoneId = (await FarmingKit.CreateZoneAsync(owner.Client)).GetProperty("id").GetGuid();
        foreach (var days in new[] { 5, 6 })
        {
            await FarmingKit.CreateLicenseAsync(owner.Client, new LicenseFields { ZoneIds = [zoneId], EffectiveDate = FarmingKit.Today.AddDays(-300), ExpiryDate = FarmingKit.Today.AddDays(days) });
        }

        var job = api.Services.GetRequiredService<ZoneLicenseReminderJob>();

        Assert.Equal(2, await job.RunOnceAsync(FarmingKit.Today));
        Assert.Equal(0, await job.RunOnceAsync(FarmingKit.Today));
        Assert.Equal(2, await api.NotificationCountAsync(owner.Account.Id, NotificationTypes.ZoneLicenseExpiry));
    }

    [Fact]
    public async Task A_provider_that_blocks_its_thread_holds_the_answer_for_the_timeout_only_and_the_others_still_go()
    {
        using var blocking = new BlockingSmsSender();
        await using var api = Host(blocking, settings: (Timeout, "1"));
        var z = await api.ZoneAsync();
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        var clock = Stopwatch.StartNew();
        var measurement = await z.RecordedAsync(9.1m, 20m, client: managerClient);
        clock.Stop();
        blocking.Release();

        Assert.True(clock.Elapsed < WellWithinTheHold, $"The answer took {clock.Elapsed}.");
        Assert.Equal(2, blocking.Calls);
        Assert.Equal(2, (await api.AlertsOfAsync(measurement.Id())).Count);
    }

    [Fact]
    public async Task A_slow_provider_within_the_limit_delivers_everything()
    {
        var gated = new GatedSmsSender();
        await using var api = Host(gated, settings: (Timeout, "30"));
        var z = await api.ZoneAsync();
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        var answer = z.RecordAsync(9.1m, 20m, client: managerClient);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        Assert.False(answer.IsCompleted);
        gated.Open();

        Assert.Equal(HttpStatusCode.Created, (await answer).StatusCode);
        Assert.Equal(2, gated.Delivered);
    }

    [Fact]
    public async Task A_provider_that_honours_its_cancellation_is_cancelled_at_the_limit()
    {
        var waiting = new WaitsForCancellationSmsSender();
        await using var api = Host(waiting, settings: (Timeout, "1"));
        var z = await api.ZoneAsync();
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        var clock = Stopwatch.StartNew();
        await z.RecordedAsync(9.1m, 20m, client: managerClient);
        clock.Stop();

        Assert.True(clock.Elapsed < WellWithinTheHold, $"The answer took {clock.Elapsed}.");
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"The answer took {clock.Elapsed}.");

        // The cancellation and the limit of the answer fall at the same moment: on a busy machine the answer can come first.
        var giveUpAt = Stopwatch.StartNew();
        while (waiting.Cancelled < 2 && giveUpAt.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(50);
        }

        Assert.Equal(2, waiting.Cancelled);
    }

    [Fact]
    public async Task The_default_limit_is_5_seconds_with_no_setting()
    {
        await using var api = Host(new HangingSmsSender());
        var z = await api.ZoneAsync();
        var (managerClient, _) = await api.StaffAsync(z.Zone);

        var clock = Stopwatch.StartNew();
        await z.RecordedAsync(9.1m, 20m, client: managerClient);
        clock.Stop();

        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(4.5), $"The answer took {clock.Elapsed}.");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(25), $"The answer took {clock.Elapsed}.");
        Assert.Contains(api.Logs.Lines, l => l.Contains("2 mandatory SMS had no answer after 5 seconds"));
    }

    // Adapted to L1: the limit is checked at startup, from 1 to 60 seconds. These counted as 1 second before.
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task A_zero_or_negative_limit_stops_the_host_at_startup(string value)
    {
        await using var api = Host(settings: (Timeout, value));

        Assert.Contains(Timeout, Assert.ThrowsAny<Exception>(() => api.CreateClient()).ToString());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    [InlineData("")]
    public async Task A_limit_that_is_not_a_whole_number_stops_the_host_at_startup(string value)
    {
        await using var api = Host(settings: (Timeout, value));

        Assert.ThrowsAny<Exception>(() => api.CreateClient());
    }

    // Adapted to L1: the largest limit is now 60 seconds, no longer the 4294967 a CancellationTokenSource takes.
    [Theory]
    [InlineData("1")]
    [InlineData("60")]
    public async Task The_smallest_and_the_largest_limit_work(string value)
    {
        await using var api = Host(settings: (Timeout, value));
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.Single(api.SmsTo(manager));
    }

    // Adapted to L1: such a limit used to answer 500 after the commit. The decision refuses it at startup, so no request gets that far;
    // MandatorySmsAfterCommitWaitTests holds the same limit past the check and shows the answer stays as it is.
    [Theory]
    [InlineData("61")]
    [InlineData("4294967")]
    [InlineData("4294968")]
    [InlineData("2147483647")]
    public async Task L1_a_limit_over_60_seconds_stops_the_host_at_startup(string value)
    {
        await using var api = Host(settings: (Timeout, value));

        Assert.Contains(Timeout, Assert.ThrowsAny<Exception>(() => api.CreateClient()).ToString());
    }

    [Fact]
    public async Task The_push_goes_out_while_the_sms_hangs()
    {
        await using var api = Host(new HangingSmsSender(), settings: (Timeout, "1"));
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        var pushes = api.Services.GetRequiredService<FakePushSender>().Inbox;
        Assert.NotEmpty(pushes.List(manager.Id.ToString()));
        Assert.NotEmpty(pushes.List(z.OwnerAccount.Id.ToString()));
    }

    // The SMS is on its way although the push never answers. The answer itself waits for that push, up to 10 seconds (MandatorySmsAfterCommitWaitTests).
    [Fact]
    public async Task The_sms_goes_out_while_an_async_push_hangs()
    {
        var push = new HangingPushSender();
        await using var api = Host(push: push);
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        var answer = z.RecordAsync(9.1m, 20m, client: managerClient);
        var delivered = await EventuallyAsync(() => api.SmsTo(manager).Count == 1, WellWithinTheHold);
        var heldByThePush = !answer.IsCompleted;
        push.Release();

        Assert.True(delivered);
        Assert.True(heldByThePush);
        Assert.Equal(HttpStatusCode.Created, (await answer).StatusCode);
    }

    // PushOutboxInterceptor: "a provider that hangs or blocks its thread must not hold the other channel back".
    [Fact]
    public async Task L2_the_sms_goes_out_while_a_push_provider_blocks_its_thread()
    {
        using var push = new BlockingPushSender();
        await using var api = Host(push: push);
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        var answer = z.RecordAsync(9.1m, 20m, client: managerClient);
        var delivered = await EventuallyAsync(() => api.SmsTo(manager).Count == 1, TimeSpan.FromSeconds(5));
        push.Release();
        await answer;

        Assert.True(delivered, "No SMS left while the push provider was blocking.");
    }

    // Client list: "Logs hold the type and the account id, never the number or the text".
    [Fact]
    public async Task L3_a_provider_error_code_that_repeats_the_number_does_not_reach_the_logs()
    {
        var sender = new RecordingSmsSender(number => Task.FromResult(DeliveryResult.Failed($"INVALID_DESTINATION {number}")));
        await using var api = Host(sender);
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        await z.RecordedAsync(9.1m, 20m, client: managerClient);

        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(manager.PhoneNumber!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_aborted_during_the_send_keeps_the_data_and_loses_no_sms()
    {
        var gated = new GatedSmsSender();
        await using var api = Host(gated, settings: (Timeout, "30"));
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);
        using var abort = new CancellationTokenSource();

        var photoKey = await managerClient.PhotoKeyAsync(z.Zone.Id);
        var answer = managerClient.PostAsJsonAsync(
            z.Base,
            new { clientOperationId = Guid.NewGuid(), raftId = z.Raft.Id, measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5), ph = 9.2m, salinity = 20m, temperature = 25m, photoKey },
            abort.Token);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        abort.Cancel();
        await Task.Delay(200);
        gated.Open();

        // The test server gives the client its answer once the pipeline has ended; the abort itself reached the request above.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);
        Assert.True(await EventuallyAsync(() => gated.Delivered == 2, WellWithinTheHold), $"Delivered {gated.Delivered}.");
        Assert.Equal(1, await api.NotificationCountAsync(manager.Id, NotificationTypes.WaterAlert));
    }
}
