using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>The mandatory SMS on a real SQL Server: the recipient query, a commit that really fails, parallel re-sends.</summary>
public class MandatorySmsSqlServerTests
{
    [SqlServerFact]
    public async Task An_alert_reaches_owner_and_zone_manager_and_not_the_others_of_the_zone()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (zone, otherZone) = (await SeedZoneAsync(database), await SeedZoneAsync(database));
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        var manager = await StaffAsync(api, StaffRole.ZoneManager, zone.Id);
        var managerElsewhere = await StaffAsync(api, StaffRole.ZoneManager, otherZone.Id);
        var managerAllZones = await StaffAsync(api, StaffRole.ZoneManager, null);
        var accountant = await StaffAsync(api, StaffRole.Accountant, zone.Id);
        var client = await LoginAsync(api, manager.Username);

        var response = await client.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements", Measurement(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(api.SmsTo(owner));
        Assert.Single(api.SmsTo(manager));
        Assert.Single(api.SmsTo(managerAllZones));
        Assert.Empty(api.SmsTo(managerElsewhere));
        Assert.Empty(api.SmsTo(accountant));
    }

    [SqlServerFact]
    public async Task Parallel_re_sends_of_one_operation_send_one_sms_per_recipient()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        var manager = await StaffAsync(api, StaffRole.ZoneManager, zone.Id);
        var client = await LoginAsync(api, manager.Username);
        var body = Measurement(Guid.NewGuid());

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync($"/api/zones/{zone.Id}/water-measurements", body)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id && n.Type == NotificationTypes.WaterAlert));
        Assert.Single(api.SmsTo(owner));
        Assert.Single(api.SmsTo(manager));
    }

    // A unique index refuses the save: the transaction of SaveChanges rolls back, with the notification rows in it.
    [SqlServerFact]
    public async Task A_save_that_rolls_back_sends_nothing_and_keeps_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        var zone = await SeedZoneAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);

        using var scope = api.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync(
            [owner.Id],
            new NotificationContent(NotificationTypes.WaterAlertWithdrawn, "Title", new UserMessage("MSG-WATER-04", "Withdrawn.")),
            CancellationToken.None);
        var duplicate = context.FarmingZones.Add(new FarmingZone { CooperativeId = zone.CooperativeId, Code = zone.Code, Name = "A second zone with the same code" });

        await Assert.ThrowsAsync<DuplicateRecordException>(() => unitOfWork.SaveChangesAsync());

        await using var db = database.CreateContext();
        Assert.Equal(0, await db.Notifications.CountAsync(n => n.RecipientAccountId == owner.Id));
        Assert.Empty(api.AllSms());

        // The failed save dropped the queue: a later save of the same request does not send what was announced for it.
        duplicate.State = EntityState.Detached;
        await unitOfWork.SaveChangesAsync();
        Assert.Empty(api.AllSms());
    }

    private static object Measurement(Guid operationId) =>
        new { clientOperationId = operationId, measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5), ph = 9.2m, salinity = 20m, confirm = true };

    /// <summary>A staff account with a verified phone: <see cref="SqlServerApiFactory.AddAccountAsync"/> gives one to an Owner only.</summary>
    private static async Task<Account> StaffAsync(SqlServerApiFactory api, StaffRole role, Guid? zoneId)
    {
        var account = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var row = await db.Accounts.SingleAsync(a => a.Id == account.Id);
        row.PhoneNumber = NewPhone();
        row.PhoneVerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await api.GrantAsync(row, role, zoneId);
        return row;
    }

    private static async Task<FarmingZone> SeedZoneAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var cooperative = await db.Cooperatives.FirstOrDefaultAsync() ?? new Cooperative { FullName = "HTX Trung Nam" };
        var zone = new FarmingZone { Cooperative = cooperative, Code = NewCode(), Name = "Vùng SQL", PhMin = 6.5m, PhMax = 8.5m };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        return zone;
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
