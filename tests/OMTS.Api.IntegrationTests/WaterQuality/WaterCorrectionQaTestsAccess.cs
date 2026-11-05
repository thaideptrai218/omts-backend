using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA of WATER-02 PRE-2, BR-WATER-06, GBR-01 and the matrix §I.4.4: who may correct or void what.</summary>
public class WaterCorrectionQaTestsAccess(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Client, Account Account)> StaffAsync(StaffRole role, FarmingZone? zone)
    {
        var password = NewPassword();
        var account = await api.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await api.GrantAsync(account, role, zone);
        return (await api.LoginAsync(account.Username, password), account);
    }

    private Task RevokeAsync(AccessGrant grant) => api.UpdateAsync<AccessGrant>(grant.Id, g => g.StatusId = StatusIds.AccessGrant.Revoked);

    private Task<AccessGrant> PairOfAsync(Account account) => api.QueryAsync(db => db.AccessGrants.SingleAsync(g => g.AccountId == account.Id));

    [Fact]
    public async Task The_own_measurement_follows_the_pair_of_the_zone_not_the_person_alone()
    {
        var z = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var (manager, account) = await StaffAsync(StaffRole.ZoneManager, z.Zone);
        var saved = await z.RecordedAsync(7m, 20m, client: manager);

        await RevokeAsync(await PairOfAsync(account));
        await api.GrantAsync(account, StaffRole.ZoneManager, other.Zone);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.CorrectAsync(saved.Id(), 7.1m, 20m, raftId: z.Raft.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.VoidAsync(saved.Id())).StatusCode);

        await api.GrantAsync(account, StaffRole.ZoneManager, z.Zone);
        Assert.Equal(HttpStatusCode.OK, (await manager.CorrectAsync(saved.Id(), 7.1m, 20m, raftId: z.Raft.Id)).StatusCode);
    }

    [Fact]
    public async Task An_all_zones_pair_keeps_the_own_measurement_and_gives_nobody_elses()
    {
        var z = await api.ZoneAsync();
        var (manager, account) = await StaffAsync(StaffRole.ZoneManager, z.Zone);
        var own = await z.RecordedAsync(7m, 20m, client: manager);
        var (colleague, _) = await StaffAsync(StaffRole.ZoneManager, z.Zone);
        var others = await z.RecordedAsync(7m, 20m, client: colleague);

        await RevokeAsync(await PairOfAsync(account));
        await api.GrantAsync(account, StaffRole.ZoneManager, zone: null);

        Assert.Equal(HttpStatusCode.OK, (await manager.CorrectAsync(own.Id(), 7.1m, 20m)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.VoidAsync(own.Id())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.CorrectAsync(others.Id(), 7.1m, 20m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.VoidAsync(others.Id())).StatusCode);
    }

    [Fact]
    public async Task The_owner_changes_any_also_of_a_locked_recorder_and_of_another_owner()
    {
        var z = await api.ZoneAsync();
        var (manager, account) = await StaffAsync(StaffRole.ZoneManager, z.Zone);
        var byLocked = await z.RecordedAsync(7m, 20m, client: manager);
        await api.UpdateAsync<Account>(account.Id, a => a.StatusId = StatusIds.Account.Locked);
        var password = NewPassword();
        var secondOwner = await api.AddAccountAsync(NewCode(), password);
        var secondOwnerClient = await api.LoginAsync(secondOwner.Username, password);
        var byOwner = await z.RecordedAsync(7m, 20m);

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.CorrectAsync(byLocked.Id(), 7.1m, 20m)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondOwnerClient.CorrectAsync(byOwner.Id(), 7.1m, 20m)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondOwnerClient.VoidAsync(byOwner.Id())).StatusCode);
    }

    [Fact]
    public async Task A_recorder_turned_accountant_cannot_change_their_own_and_an_all_zones_accountant_nothing()
    {
        var z = await api.ZoneAsync();
        var (client, account) = await StaffAsync(StaffRole.ZoneManager, z.Zone);
        var own = await z.RecordedAsync(7m, 20m, client: client);
        await RevokeAsync(await PairOfAsync(account));
        await api.GrantAsync(account, StaffRole.Accountant, z.Zone);
        var (allZones, _) = await StaffAsync(StaffRole.Accountant, zone: null);

        foreach (var caller in new[] { client, allZones })
        {
            foreach (var response in new[] { await caller.CorrectAsync(own.Id(), 7.1m, 20m, raftId: z.Raft.Id), await caller.VoidAsync(own.Id()) })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }
        }
    }

    [Fact]
    public async Task A_trader_and_an_anonymous_caller_are_refused_and_nothing_changes()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(9m, 20m);
        var password = NewPassword();
        var trader = await api.AddAccountAsync(NewCode(), password, SubjectType.Trader);
        var traderClient = await api.LoginAsync(trader.Username, password);
        var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await traderClient.CorrectAsync(saved.Id(), 7m, 20m, raftId: z.Raft.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await traderClient.VoidAsync(saved.Id())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.CorrectAsync(saved.Id(), 7m, 20m, raftId: z.Raft.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.VoidAsync(saved.Id())).StatusCode);

        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == saved.Id()));
        Assert.Equal((9m, StatusIds.WaterMeasurement.Active), (stored.Ph, stored.StatusId));
        Assert.Empty(await api.WithdrawalsOfAsync(saved.Id()));
    }

    [Fact]
    public async Task The_order_is_who_then_status_then_values_as_the_client_list_says()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await StaffAsync(StaffRole.ZoneManager, z.Zone);
        var others = await z.RecordedAsync(7m, 20m);
        var own = await z.RecordedAsync(7m, 20m, client: manager);
        await manager.VoidAsync(own.Id());

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.CorrectAsync(others.Id(), 99m, 20m, reason: null, raftId: z.Raft.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.VoidAsync(others.Id(), reason: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.CorrectAsync(own.Id(), 99m, 20m, reason: null, raftId: z.Raft.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.VoidAsync(own.Id(), reason: null)).StatusCode);
    }

    [Fact]
    public async Task A_zone_discontinued_then_resumed_opens_again_and_a_voided_one_stays_voided_first()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var voided = await z.RecordedAsync(7m, 20m);
        await z.Owner.VoidAsync(voided.Id());
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);

        var refused = await z.Owner.VoidAsync(voided.Id());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Water measurement is Voided", (await refused.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));

        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.InUse);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.CorrectAsync(saved.Id(), 7.1m, 20m)).StatusCode);
    }

    // GBR-02: the person who did it, and the recorder stays who recorded it.
    [Fact]
    public async Task The_audit_names_who_acted_and_the_recorder_does_not_change()
    {
        var z = await api.ZoneAsync();
        var (manager, account) = await StaffAsync(StaffRole.ZoneManager, z.Zone);
        var saved = await z.RecordedAsync(7m, 20m, client: manager);

        await manager.CorrectAsync(saved.Id(), 7.1m, 20m);
        await z.Owner.CorrectAsync(saved.Id(), 7.2m, 20m);
        await z.Owner.VoidAsync(saved.Id());

        var audit = await api.AuditOfAsync(saved.Id());
        Assert.Single(audit, e => e.Action == "WaterMeasurement.Corrected" && e.ActorAccountId == account.Id);
        Assert.Single(audit, e => e.Action == "WaterMeasurement.Corrected" && e.ActorAccountId == z.OwnerAccount.Id);
        Assert.Single(audit, e => e.Action == "WaterMeasurement.Voided" && e.ActorAccountId == z.OwnerAccount.Id);
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == saved.Id()));
        Assert.Equal(account.Id, stored.MeasuredByAccountId);
    }
}
