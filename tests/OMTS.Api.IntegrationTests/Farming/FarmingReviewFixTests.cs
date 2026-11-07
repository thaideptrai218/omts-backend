using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>Review follow-ups of OMTS-210: reminder candidates and catch-up, paging.</summary>
public class FarmingReviewFixTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // The reminder job loads only licenses of In Use zones with a notice due, never far-off or already-told ones.
    [Fact]
    public async Task Reminder_candidates_are_only_licenses_with_a_notice_due()
    {
        var today = Today;
        var zoneId = (await factory.AddZoneAsync(NewCode())).Id;
        var stoppedZoneId = (await factory.AddZoneAsync(NewCode())).Id;
        await factory.UpdateAsync<FarmingZone>(stoppedZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        var due = License(zoneId, today.AddDays(10));
        var far = License(zoneId, today.AddDays(90));
        var told = License(zoneId, today.AddDays(5), expiringSent: true);
        var expired = License(zoneId, today.AddDays(-1), expiringSent: true);
        var expiredTold = License(zoneId, today.AddDays(-3), expiringSent: true, expiredSent: true);
        var noExpiry = License(zoneId, null);
        var zoneStopped = License(stoppedZoneId, today.AddDays(10));
        await factory.UseDbAsync(db => db.ZoneComplianceDocuments.AddRange(due, far, told, expired, expiredTold, noExpiry, zoneStopped));

        using var scope = factory.Services.CreateScope();
        var ids = await scope.ServiceProvider.GetRequiredService<ZoneLicenseReminderService>().CandidateIdsAsync(today, default);

        Assert.Contains(due.Id, ids);
        Assert.Contains(expired.Id, ids);
        Assert.DoesNotContain(far.Id, ids);
        Assert.DoesNotContain(told.Id, ids);
        Assert.DoesNotContain(expiredTold.Id, ids);
        Assert.DoesNotContain(noExpiry.Id, ids);
        Assert.DoesNotContain(zoneStopped.Id, ids);
    }

    // A host started after the daily run time (deploy, restart) runs once at start.
    [Theory]
    [InlineData("2026-10-23T00:30:00Z", true)]
    [InlineData("2026-10-22T23:59:00Z", false)]
    [InlineData("2026-10-22T16:59:00Z", true)]
    [InlineData("2026-10-23T00:00:00Z", true)]
    public void Reminder_catches_up_when_started_after_the_run_time(string utc, bool catchUp)
    {
        Assert.Equal(catchUp, ZoneLicenseReminderHostedService.IsPastRunTime(DateTimeOffset.Parse(utc), new TimeOnly(7, 0)));
    }

    [Fact]
    public async Task Huge_page_number_is_not_a_500()
    {
        var owner = (await factory.OwnerAsync()).Client;

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/zones?page={int.MaxValue}&pageSize=100")).StatusCode);
    }

    private static ZoneComplianceDocument License(Guid zoneId, DateOnly? expiry, bool expiringSent = false, bool expiredSent = false) => new()
    {
        ZoneId = zoneId,
        DocumentType = "AquacultureLicense",
        DocumentNumber = $"GP-{NewCode()}",
        ExpiryDate = expiry,
        ExpiringReminderSentAt = expiringSent ? DateTimeOffset.UtcNow.AddDays(-1) : null,
        ExpiredReminderSentAt = expiredSent ? DateTimeOffset.UtcNow.AddDays(-1) : null,
    };
}
