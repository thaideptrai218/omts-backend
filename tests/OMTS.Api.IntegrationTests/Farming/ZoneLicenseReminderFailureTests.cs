using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// The real API whose notification dispatch fails for one license (its number contains <see cref="Marker"/>), as when
/// the notification store or push gateway breaks on one record; every other notice goes through the real dispatcher.
/// </summary>
public sealed class FailingNoticeApiFactory : AuthApiFactory
{
    public const string Marker = "FAILNOTICE";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddScoped<NotificationDispatcher>();
            services.AddScoped<INotificationDispatcher>(provider => new FailingForOneLicense(provider.GetRequiredService<NotificationDispatcher>()));
        });
    }

    private sealed class FailingForOneLicense(INotificationDispatcher inner) : INotificationDispatcher
    {
        public Task NotifyAsync(
            IReadOnlyCollection<Guid> recipientAccountIds, string type, string title, string? body, string? relatedRecordType,
            string? relatedRecordCode, bool isMandatory, CancellationToken cancellationToken) =>
            body?.Contains(Marker, StringComparison.Ordinal) == true
                ? throw new InvalidOperationException("Notification store unavailable for this record.")
                : inner.NotifyAsync(recipientAccountIds, type, title, body, relatedRecordType, relatedRecordCode, isMandatory, cancellationToken);
    }
}

/// <summary>FARM-03 reminder job: one failing license is logged and skipped, the others continue.</summary>
public class ZoneLicenseReminderFailureTests(FailingNoticeApiFactory factory) : IClassFixture<FailingNoticeApiFactory>
{
    [Fact]
    public async Task A_failing_license_is_logged_and_skipped_while_the_others_are_reminded()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var failing = await owner.Client.CreateLicenseAsync(new LicenseFields
        {
            DocumentNumber = $"GP-{FailingNoticeApiFactory.Marker}-{NewCode()}", ZoneIds = [zoneId], EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(5),
        });
        var fine = await owner.Client.CreateLicenseAsync(new LicenseFields { ZoneIds = [zoneId], EffectiveDate = Today.AddDays(-300), ExpiryDate = Today.AddDays(5) });
        var job = factory.Services.GetRequiredService<ZoneLicenseReminderJob>();

        Assert.Equal(1, await job.RunOnceAsync(Today));
        Assert.Equal(0, await job.RunOnceAsync(Today));

        Assert.NotEmpty(await NoticesAsync(fine.Id()));
        Assert.Empty(await NoticesAsync(failing.Id()));
        // Nothing of the failed license was saved, so the next run tries it again.
        var unsent = await factory.QueryAsync(db => db.ZoneComplianceDocuments.SingleAsync(d => d.Id == failing.Id()));
        Assert.Null(unsent.ExpiringReminderSentAt);
        Assert.Equal(2, factory.Logs.Lines.Count(l => l.Contains($"Zone license reminder failed for license {failing.Id()}", StringComparison.Ordinal)));
    }

    private Task<List<OMTS.Domain.Entities.Platform.Notification>> NoticesAsync(Guid licenseId)
    {
        var code = licenseId.ToString();
        return factory.QueryAsync(db => db.Notifications.Where(n => n.Type == NotificationTypes.ZoneLicenseExpiry && n.RelatedRecordCode == code).ToListAsync());
    }
}
