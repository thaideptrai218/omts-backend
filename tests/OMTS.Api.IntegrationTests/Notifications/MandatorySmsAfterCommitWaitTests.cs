using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Features.Notifications;
using OMTS.Infrastructure.Messaging;
using static OMTS.Api.IntegrationTests.Notifications.MandatorySmsKit;
using static OMTS.Api.IntegrationTests.Notifications.MandatorySmsQaKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>What the answer waits for once the save has committed: a bound in all, and nothing after the commit can answer 500 (GFR-06).</summary>
public class MandatorySmsAfterCommitWaitTests
{
    // Past the startup check on purpose: whatever goes wrong in the delivery after the commit stays there.
    [Fact]
    public async Task L1_a_limit_that_passes_the_startup_check_and_cannot_be_used_leaves_the_answer_as_it_is()
    {
        var unusable = Options.Create(new SmsOptions { Provider = SmsOptions.FakeProvider, MandatoryTimeoutSeconds = int.MaxValue });
        await using var api = new WaterApiFactory(services: services => services.AddSingleton<IOptions<SmsOptions>>(unusable));
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);

        var response = await z.RecordAsync(9.1m, 20m, client: managerClient);

        var rows = await api.NotificationCountAsync(manager.Id, NotificationTypes.WaterAlert);
        Assert.Equal((HttpStatusCode.Created, 1, 0), (response.StatusCode, rows, api.SmsTo(manager).Count));
        Assert.Contains(api.Logs.Lines, l => l.Contains("2 mandatory SMS not sent: ArgumentOutOfRangeException"));
    }
}
