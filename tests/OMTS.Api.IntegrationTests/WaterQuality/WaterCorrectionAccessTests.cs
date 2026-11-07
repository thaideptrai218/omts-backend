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

/// <summary>
/// WATER-02 PRE-2 and BR-WATER-06: a Zone Manager changes only what they recorded, in a zone they can still access, the
/// Cooperative Owner any measurement, and nobody touches a Discontinued zone.
/// </summary>
public class WaterCorrectionAccessTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_zone_manager_corrects_and_voids_their_own_measurement()
    {
        var z = await api.ZoneAsync();
        var (manager, account) = await api.StaffAsync(z.Zone);
        var toCorrect = await z.RecordedAsync(7m, 20m, client: manager);
        var toVoid = await z.RecordedAsync(7m, 20m, client: manager);

        var corrected = await manager.CorrectAsync(toCorrect.Id(), 7.1m, 20m);
        var voided = await manager.VoidAsync(toVoid.Id());

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var body = await corrected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(account.Id, body.GetProperty("measuredBy").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_zone_manager_cannot_change_what_another_person_recorded_even_at_all_zones()
    {
        var z = await api.ZoneAsync();
        var (colleague, _) = await api.StaffAsync(z.Zone);
        var allZones = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(allZones, StaffRole.ZoneManager, zone: null);
        var byColleague = await z.RecordedAsync(7m, 20m, client: colleague);
        var byOwner = await z.RecordedAsync(7m, 20m);
        var (manager, _) = await api.StaffAsync(z.Zone);
        var allZonesManager = await api.LoginAsync(allZones.Username);

        foreach (var client in new[] { manager, allZonesManager })
        {
            foreach (var id in new[] { byColleague.Id(), byOwner.Id() })
            {
                foreach (var response in new[] { await client.CorrectAsync(id, 7.1m, 20m), await client.VoidAsync(id) })
                {
                    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                    Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
                }
            }
        }

        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().Where(m => m.ZoneId == z.Zone.Id).ToListAsync());
        Assert.All(stored, m => Assert.Equal((7m, StatusIds.WaterMeasurement.Active), (m.Ph, m.StatusId)));
        Assert.DoesNotContain(await api.AuditOfAsync(byColleague.Id()), e => e.Action != "WaterMeasurement.Created");
    }

    [Fact]
    public async Task The_owner_corrects_and_voids_a_measurement_of_a_zone_manager_who_stays_its_recorder()
    {
        var z = await api.ZoneAsync();
        var (manager, account) = await api.StaffAsync(z.Zone);
        var toCorrect = await z.RecordedAsync(7m, 20m, client: manager);
        var toVoid = await z.RecordedAsync(7m, 20m, client: manager);

        var corrected = await z.Owner.CorrectAsync(toCorrect.Id(), 7.1m, 20m);
        var voided = await z.Owner.VoidAsync(toVoid.Id());

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.Equal(account.Id, (await corrected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("measuredBy").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_recorder_who_lost_the_zone_and_other_roles_cannot_change_it()
    {
        var z = await api.ZoneAsync();
        var (former, account) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(7m, 20m, client: former);
        var grant = await api.QueryAsync(db => db.AccessGrants.SingleAsync(g => g.AccountId == account.Id));
        await api.UpdateAsync<AccessGrant>(grant.Id, g => g.StatusId = StatusIds.AccessGrant.Revoked);
        // WATER-02 is No for the Accountant (§I.4.4).
        var (accountant, _) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var (outsider, _) = await api.StaffAsync((await api.ZoneAsync()).Zone);

        foreach (var client in new[] { former, accountant, outsider })
        {
            foreach (var response in new[] { await client.CorrectAsync(saved.Id(), 7.1m, 20m, raftId: z.Raft.Id), await client.VoidAsync(saved.Id()) })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }
        }

        Assert.Equal(StatusIds.WaterMeasurement.Active, await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == saved.Id()).Select(m => m.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task A_measurement_of_a_discontinued_zone_can_only_be_viewed_by_anyone()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(9m, 20m, client: manager);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);

        foreach (var client in new[] { z.Owner, manager })
        {
            foreach (var response in new[] { await client.CorrectAsync(saved.Id(), 7m, 20m), await client.VoidAsync(saved.Id()) })
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("MSG-GEN-09", await response.ErrorCodeAsync());
                Assert.Equal("Farming zone is Discontinued and cannot be used for this action.", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
            }

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/water-measurements/{saved.Id()}")).StatusCode);
        }

        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == saved.Id()));
        Assert.Equal((9m, StatusIds.WaterMeasurement.Active), (stored.Ph, stored.StatusId));
        Assert.Empty(await api.WithdrawalsOfAsync(saved.Id()));
    }

    // Who may act comes first: another person's measurement of a Discontinued zone answers 403, as for an In Use zone.
    [Fact]
    public async Task A_zone_manager_is_refused_on_another_persons_measurement_before_the_status_of_the_zone()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(7m, 20m);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);

        var response = await manager.VoidAsync(saved.Id());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
