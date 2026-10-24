using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.WaterQuality;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// No personal data in the logs of the water flows (QA red team): recording with an alert and push, a failed
/// recognition, correcting, voiding, exporting, reading and the overdue reminder log ids and codes, never a
/// phone number, email or display name.
/// </summary>
public class WaterLogPrivacyRedTeamTests
{
    [Fact]
    public async Task Water_flows_never_log_phone_numbers_emails_or_display_names()
    {
        await using var api = new WaterApiFactory(new Dictionary<string, string> { ["Ocr:Fake:Mode"] = "Fail" });
        var z = await api.ZoneAsync();
        const string displayName = "Nguyễn Thị Quản Lý";
        const string email = "quanly.private@example.com";
        var manager = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a =>
        {
            a.DisplayName = displayName;
            a.Email = email;
            a.EmailVerifiedAt = DateTimeOffset.UtcNow;
        });
        var role = await api.AddRoleAsync($"Water {NewCode()}", (PermissionFunctions.WaterMeasurements, View: true, Create: true, Edit: true));
        await api.GrantAsync(manager, role, z.Zone);
        var client = await api.LoginAsync(manager.Username);

        var alerting = await z.RecordedAsync(9.4m, 20m, client: client, measuredAt: DateTimeOffset.UtcNow.AddDays(-40));
        Assert.Equal(HttpStatusCode.OK, (await z.RecognizeAsync(client: client)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.CorrectAsync(alerting.Id(), 9.6m, 20m, recordVersion: 1, measuredAt: DateTimeOffset.UtcNow.AddDays(-40))).StatusCode);
        var other = await z.RecordedAsync(7m, 20m, client: client);
        Assert.Equal(HttpStatusCode.OK, (await client.VoidAsync(other.Id(), 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.GetAsync($"{z.Base}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/water-measurements/{alerting.Id()}")).StatusCode);
        using (var scope = api.Services.CreateScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>()
                .RunOnceAsync(TimeProvider.System.Today(), CancellationToken.None));
        }

        var lines = api.Logs.Lines.ToList();
        Assert.NotEmpty(lines);
        foreach (var secret in new[] { manager.PhoneNumber!, z.OwnerAccount.PhoneNumber!, email, displayName })
        {
            Assert.DoesNotContain(lines, line => line.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }
}
