using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>MSG-CYCLE-04: the Owners are told once of a new log, not of a correction or removal, and not of their own log (PM decision OMTS-216).</summary>
public class CultivationLogNotificationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Type = "CULTIVATION_LOG_RECORDED";

    private Task<List<Notification>> OfLogAsync(Guid logId) =>
        factory.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == logId).ToListAsync());

    [Fact]
    public async Task A_log_of_a_zone_manager_tells_every_owner_once_and_not_the_manager()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var visit = Today.AddDays(-2);

        var log = await manager.Client.RecordLogAsync(setup.CycleId, new LogFields { VisitDate = visit });

        var raftCode = log.Text("raftCode");
        var rows = await OfLogAsync(log.Id());
        Assert.Contains(rows, n => n.RecipientAccountId == setup.Owner.Account.Id);
        Assert.DoesNotContain(rows, n => n.RecipientAccountId == manager.Account.Id);
        Assert.Equal(rows.Count, rows.Select(n => n.RecipientAccountId!.Value).Distinct().Count());
        Assert.All(rows, n =>
        {
            Assert.Equal((Type, "MSG-CYCLE-04", false), (n.Type, n.MessageCode, n.IsMandatory));
            Assert.Equal($"Raft {raftCode}: a cultivation log was recorded for the visit on {visit:dd/MM/yyyy}. Please review it.", n.Body);
            Assert.Equal(("CultivationLog", raftCode), (n.RelatedRecordType, n.RelatedRecordCode));
            using var parameters = JsonDocument.Parse(n.MessageParams!);
            Assert.Equal(raftCode, parameters.RootElement.Text("raft_code"));
            Assert.Equal(visit.ToString("yyyy-MM-dd"), parameters.RootElement.Text("visit_date"));
        });
    }

    [Fact]
    public async Task An_owner_is_not_told_of_their_own_log_and_another_owner_is()
    {
        var setup = await factory.CycleAsync();
        var other = await factory.OwnerAsync();

        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);

        var told = (await OfLogAsync(log.Id())).Select(n => n.RecipientAccountId!.Value).ToList();
        Assert.DoesNotContain(setup.Owner.Account.Id, told);
        Assert.Contains(other.Account.Id, told);
    }

    [Fact]
    public async Task A_correction_and_a_removal_tell_nobody_again()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var log = await manager.Client.RecordLogAsync(setup.CycleId);
        var told = (await OfLogAsync(log.Id())).Count;
        Assert.True(told > 0);
        var total = await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == Type));

        var corrected = new LogFields { Notes = "corrected", Reason = "wrong note", RowVersion = await factory.LogVersionAsync(log.Id()), KeepPhotoKeys = log.PhotoKeys(), Photos = [Png] };
        await (await manager.Client.PutLogAsync(log.Id(), corrected)).ReadAsync(HttpStatusCode.OK);
        await (await setup.Owner.Client.PutLogAsync(log.Id(), corrected with { Notes = "by the owner", Photos = [] })).ReadAsync(HttpStatusCode.OK);
        await (await manager.Client.RemoveLogAsync(log.Id(), "wrong raft", corrected.RowVersion)).ReadAsync(HttpStatusCode.NoContent);

        Assert.Equal(told, (await OfLogAsync(log.Id())).Count);
        Assert.Equal(total, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == Type)));
    }

    [Fact]
    public async Task A_refused_log_tells_nobody_and_a_locked_owner_is_not_told()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        await factory.UpdateAsync<Account>(setup.Owner.Account.Id, a => a.StatusId = StatusIds.Account.Locked);
        var total = await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == Type));

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.Client.PostLogAsync(setup.CycleId, new LogFields { Photos = [FarmingTestKit.Pdf] })).StatusCode);
        Assert.Equal(total, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == Type)));

        var log = await manager.Client.RecordLogAsync(setup.CycleId);

        Assert.DoesNotContain(await OfLogAsync(log.Id()), n => n.RecipientAccountId == setup.Owner.Account.Id);
    }

    // CYCLE-05.2: the notification opens the log; once the log is removed it answers MSG-ACCOUNT-22 with the name of the record and the raft.
    [Fact]
    public async Task The_notification_opens_the_log_until_it_is_removed()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var log = await manager.Client.RecordLogAsync(setup.CycleId);
        var row = (await OfLogAsync(log.Id())).Single(n => n.RecipientAccountId == setup.Owner.Account.Id);
        var owner = setup.Owner.Client;

        var opened = await owner.GetFromJsonAsync<JsonElement>($"/api/me/notifications/{row.Id}/open");
        Assert.Equal("CultivationLog", opened.Text("recordType"));
        Assert.Equal(log.Id(), opened.GetProperty("recordId").GetGuid());
        Assert.Equal(setup.ZoneId, opened.GetProperty("zoneId").GetGuid());

        await (await manager.Client.RemoveLogAsync(log.Id(), "wrong raft", "")).ReadAsync(HttpStatusCode.NoContent);

        var gone = await (await owner.GetAsync($"/api/me/notifications/{row.Id}/open")).ReadAsync(HttpStatusCode.NotFound);
        Assert.Equal("MSG-ACCOUNT-22", gone.Text("code"));
        Assert.Contains("Cultivation log", gone.Text("detail"));
    }
}
