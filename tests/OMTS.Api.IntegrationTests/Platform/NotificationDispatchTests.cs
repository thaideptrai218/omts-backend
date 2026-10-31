using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

public class NotificationDispatchTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Zone_alert_reaches_the_owner_and_granted_zone_managers_only()
    {
        var (zoneA, zoneB) = (await factory.AddZoneAsync(Code()), await factory.AddZoneAsync(Code()));
        var setup = await ZoneRecipientsSetup.CreateAsync(factory, zoneA, zoneB);

        var recipients = await NotifyAsync(factory, zoneA.Id);

        Assert.Contains(setup.Owner, recipients);
        Assert.Contains(setup.ManagerOfA, recipients);
        Assert.Contains(setup.ManagerAllZones, recipients);
        Assert.DoesNotContain(setup.ManagerOfB, recipients);
        Assert.DoesNotContain(setup.RevokedInA, recipients);
        Assert.DoesNotContain(setup.AccountantOfA, recipients);
        Assert.DoesNotContain(setup.LockedManagerOfA, recipients);

        using var scope = factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Notifications
            .Where(n => n.RelatedRecordCode == zoneA.Code).Select(n => n.RecipientAccountId).ToListAsync();
        Assert.Equal(recipients.Order(), rows.Order());
        var pushed = factory.Services.GetRequiredService<FakePushSender>().Inbox.List(setup.ManagerOfA.ToString());
        Assert.Contains(pushed, m => m.Subject == "Water alert");
    }

    // Same rule as AccountAccess: records with no zone follow the role only (BR-FARM-08), so an Accountant pair in any
    // zone counts for supplier payments; a Zone Manager, whose role does not hold them, never does.
    [Fact]
    public async Task Function_on_records_with_no_zone_counts_any_pair_of_a_role_holding_it()
    {
        var (zone, otherZone) = (await factory.AddZoneAsync(Code()), await factory.AddZoneAsync(Code()));
        var accountantElsewhere = await factory.AddAccountAsync(Code(), AcsTestKit.NewPassword(), SubjectType.Staff);
        var accountantAllZones = await factory.AddAccountAsync(Code(), AcsTestKit.NewPassword(), SubjectType.Staff);
        var manager = await factory.AddAccountAsync(Code(), AcsTestKit.NewPassword(), SubjectType.Staff);
        await factory.GrantAsync(accountantElsewhere, StaffRole.Accountant, otherZone);
        await factory.GrantAsync(accountantAllZones, StaffRole.Accountant, null);
        await factory.GrantAsync(manager, StaffRole.ZoneManager, null);

        using var scope = factory.Services.CreateScope();
        var recipients = await scope.ServiceProvider.GetRequiredService<IZoneRecipientQuery>()
            .GetRecipientsAsync(zone.Id, PermissionFunctions.SupplierPayments, PermissionAction.View, CancellationToken.None);

        Assert.Contains(accountantElsewhere.Id, recipients);
        Assert.Contains(accountantAllZones.Id, recipients);
        Assert.DoesNotContain(manager.Id, recipients);
    }

    /// <summary>Recipients query + dispatch + save, as ENV-01 does for a water alert.</summary>
    internal static async Task<IReadOnlyList<Guid>> NotifyAsync(AuthApiFactory api, Guid zoneId)
    {
        using var scope = api.Services.CreateScope();
        var services = scope.ServiceProvider;
        var zoneCode = (await services.GetRequiredService<OmtsDbContext>().FarmingZones.SingleAsync(z => z.Id == zoneId)).Code;
        var recipients = await services.GetRequiredService<IZoneRecipientQuery>()
            .GetRecipientsAsync(zoneId, PermissionFunctions.WaterMeasurements, PermissionAction.View, CancellationToken.None);

        await services.GetRequiredService<INotificationDispatcher>().NotifyAsync(
            recipients,
            new NotificationContent(
                NotificationTypes.WaterAlert,
                "Water alert",
                UserMessages.WaterAlert("", "Zone", "–", "08:00 20/10/2026", "pH 9.1 is too high (allowed 7.5–8.5)"),
                new RelatedRecord(RelatedRecordTypes.FarmingZone, zoneId, zoneCode)),
            CancellationToken.None);
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return recipients;
    }

    internal static string Code() => Guid.NewGuid().ToString("N")[..10];
}

/// <summary>A failing push provider must never roll back the saved notifications.</summary>
public class PushFailureTests(PushFailureTests.ThrowingPushApiFactory factory) : IClassFixture<PushFailureTests.ThrowingPushApiFactory>
{
    [Fact]
    public async Task Push_failure_keeps_the_saved_notification_rows()
    {
        var zone = await factory.AddZoneAsync(NotificationDispatchTests.Code());
        await factory.AddAccountAsync(NotificationDispatchTests.Code(), AcsTestKit.NewPassword());

        var recipients = await NotificationDispatchTests.NotifyAsync(factory, zone.Id);

        using var scope = factory.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Notifications.CountAsync(n => n.RelatedRecordCode == zone.Code);
        Assert.NotEmpty(recipients);
        Assert.Equal(recipients.Count, saved);
        Assert.Contains(factory.Logs.Lines, line => line.Contains("Push WATER_ALERT"));
    }

    public sealed class ThrowingPushApiFactory : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<IPushSender, ThrowingPushSender>());
        }
    }

    private sealed class ThrowingPushSender : IPushSender
    {
        public Task<DeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("push provider down");
    }
}

/// <summary>Accounts around two zones: who must and must not hear about zone A.</summary>
internal sealed record ZoneRecipientsSetup(
    Guid Owner, Guid ManagerOfA, Guid ManagerAllZones, Guid ManagerOfB, Guid RevokedInA, Guid AccountantOfA, Guid LockedManagerOfA)
{
    public static async Task<ZoneRecipientsSetup> CreateAsync(AuthApiFactory api, FarmingZone zoneA, FarmingZone zoneB)
    {
        var password = AcsTestKit.NewPassword();

        async Task<Guid> StaffAsync(
            FarmingZone? zone, Action<Domain.Entities.Identity.AccessGrant>? grant = null, bool locked = false, StaffRole role = StaffRole.ZoneManager)
        {
            var account = await api.AddAccountAsync(NotificationDispatchTests.Code(), password, SubjectType.Staff,
                a => a.StatusId = locked ? StatusIds.Account.Locked : StatusIds.Account.Active);
            await api.GrantAsync(account, role, zone, grant);
            return account.Id;
        }

        return new ZoneRecipientsSetup(
            (await api.AddAccountAsync(NotificationDispatchTests.Code(), password)).Id,
            await StaffAsync(zoneA),
            await StaffAsync(null),
            await StaffAsync(zoneB),
            await StaffAsync(zoneA, g => g.StatusId = StatusIds.AccessGrant.Revoked),
            // The Accountant role holds no WATER_MEASUREMENTS right (§I.4.4 WATER-01…03 No).
            await StaffAsync(zoneA, role: StaffRole.Accountant),
            await StaffAsync(zoneA, locked: true));
    }
}
