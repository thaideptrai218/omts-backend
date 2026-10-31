using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// ENV-01…ENV-04 roles and access (QA red team): Accountant and Trader never reach water data, thresholds are
/// set by the Cooperative Owner only, a record id never opens another zone, each action needs its own right,
/// and a client operation id is scoped to one account and one zone.
/// </summary>
public class WaterAccessRedTeamTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Accountant_at_all_zones_and_trader_get_MSG02_on_every_water_endpoint_and_change_nothing()
    {
        var z = await api.ZoneAsync();
        var confirmed = await z.RecordedAsync(7m, 20m);
        var accountant = await AccountantAtAllZonesAsync();
        var trader = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password, SubjectType.Trader)).Username);

        foreach (var client in new[] { accountant, trader })
        {
            var calls = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
            {
                ("history", () => client.GetAsync(z.Base)),
                ("chart", () => client.GetAsync($"{z.Base}/chart")),
                ("export", () => client.GetAsync($"{z.Base}/export")),
                ("detail", () => client.GetAsync($"/api/water-measurements/{confirmed.Id()}")),
                ("record", () => z.RecordAsync(7m, 20m, client: client)),
                ("recognize", () => z.RecognizeAsync(client: client)),
                ("correct", () => client.CorrectAsync(confirmed.Id(), 7.1m, 20m, 1)),
                ("void", () => client.VoidAsync(confirmed.Id(), 1)),
                ("editing", () => client.PostAsync($"/api/water-measurements/{confirmed.Id()}/editing", null)),
                ("thresholds list", () => client.GetAsync("/api/water-thresholds")),
                ("zone thresholds", () => client.GetAsync($"/api/zones/{z.Zone.Id}/water-thresholds")),
                ("set zone thresholds", () => client.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 1m, phMax = 2m })),
                ("set all thresholds", () => client.PutAsJsonAsync("/api/water-thresholds", new { phMin = 1m, phMax = 2m })),
            };

            foreach (var (name, call) in calls)
            {
                var response = await call();
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: {(int)response.StatusCode}");
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }
        }

        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().Where(m => m.ZoneId == z.Zone.Id).ToListAsync());
        var single = Assert.Single(stored);
        Assert.Equal(StatusIds.WaterMeasurement.Confirmed, single.StatusId);
        var zone = await api.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(x => x.Id == z.Zone.Id));
        Assert.Equal((6.5m, 8.5m), (zone.PhMin!.Value, zone.PhMax!.Value));
    }

    [Fact]
    public async Task Staff_holding_every_water_right_at_all_zones_still_cannot_set_thresholds()
    {
        var z = await api.ZoneAsync();
        var role = await AddWaterRoleAsync(view: true, create: true, edit: true, export: true);
        var account = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(account, role, zone: null);
        var staff = await api.LoginAsync(account.Username);

        var read = await staff.GetAsync($"/api/zones/{z.Zone.Id}/water-thresholds");
        var setOne = await staff.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new { phMin = 1m, phMax = 2m, salinityMin = 1m, salinityMax = 2m });
        var setAll = await staff.PutAsJsonAsync("/api/water-thresholds", new { phMin = 1m, phMax = 2m, salinityMin = 1m, salinityMax = 2m });

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        foreach (var response in new[] { setOne, setAll })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }

        var zone = await api.QueryAsync(db => db.FarmingZones.AsNoTracking().SingleAsync(x => x.Id == z.Zone.Id));
        Assert.Equal((6.5m, 8.5m, 15m, 30m), (zone.PhMin!.Value, zone.PhMax!.Value, zone.SalinityMin!.Value, zone.SalinityMax!.Value));
        Assert.DoesNotContain(await api.AuditOfAsync(z.Zone.Id), e => e.Action == "WaterThreshold.Updated");
    }

    [Fact]
    public async Task Manager_of_zone_A_cannot_recognize_void_or_stop_editing_a_record_of_zone_B_by_id()
    {
        var zoneB = await api.ZoneAsync();
        var draftId = (await (await zoneB.RecognizeAsync("ocr-fail")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("measurement").Id();
        var confirmed = await zoneB.RecordedAsync(7m, 20m);
        var (managerOfA, _) = await api.StaffAsync((await api.ZoneAsync()).Zone, create: true, edit: true, export: true);

        var responses = new[]
        {
            await managerOfA.PostAsync($"/api/water-measurements/{draftId}/recognize", null),
            await managerOfA.VoidAsync(draftId, 1),
            await managerOfA.DeleteAsync($"/api/water-measurements/{confirmed.Id()}/editing"),
            await managerOfA.GetAsync($"/api/water-measurements/{confirmed.Id()}"),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            Assert.DoesNotContain(draftId.ToString(), await response.Content.ReadAsStringAsync());
        }

        var draft = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == draftId));
        Assert.Equal(StatusIds.WaterMeasurement.Draft, draft.StatusId);
        Assert.Null(draft.OcrPh);
        Assert.Null(draft.VoidReason);
    }

    [Fact]
    public async Task Each_action_needs_its_own_right_view_does_not_confirm_create_does_not_void_edit_does_not_confirm()
    {
        var z = await api.ZoneAsync();
        var draft = await z.RecordedAsync(7m, 20m, confirm: false);
        var (viewer, _) = await api.StaffAsync(z.Zone);
        var (creator, _) = await api.StaffAsync(z.Zone, create: true);
        var (editor, _) = await api.StaffAsync(z.Zone, edit: true);

        var responses = new[]
        {
            await viewer.PostAsJsonAsync($"/api/water-measurements/{draft.Id()}/confirm", new { }),
            await creator.VoidAsync(draft.Id(), 1),
            await creator.PostAsync($"/api/water-measurements/{draft.Id()}/editing", null),
            await editor.PostAsJsonAsync($"/api/water-measurements/{draft.Id()}/confirm", new { }),
            await editor.PostAsync($"/api/water-measurements/{draft.Id()}/recognize", null),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }

        Assert.Equal(StatusIds.WaterMeasurement.Draft,
            await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == draft.Id()).Select(m => m.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task Staff_whose_role_lacks_view_cannot_read_a_record_of_their_zone()
    {
        var z = await api.ZoneAsync();
        var confirmed = await z.RecordedAsync(7m, 20m);
        var (noView, _) = await api.StaffAsync(z.Zone, view: false, create: true);

        var detail = await noView.GetAsync($"/api/water-measurements/{confirmed.Id()}");
        var history = await noView.GetAsync(z.Base);

        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
        Assert.Equal("MSG-GEN-08", await detail.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
    }

    [Fact]
    public async Task Re_sending_a_saved_operation_after_the_grant_was_revoked_is_refused_not_answered_with_the_record()
    {
        var z = await api.ZoneAsync();
        var (manager, account) = await api.StaffAsync(z.Zone, create: true);
        var operationId = Guid.NewGuid();
        var first = await z.RecordAsync(7m, 20m, clientOperationId: operationId, client: manager);
        var savedId = (await first.Content.ReadFromJsonAsync<JsonElement>()).Id();
        var grant = await api.QueryAsync(db => db.AccessGrants.SingleAsync(g => g.AccountId == account.Id));
        await api.UpdateAsync<AccessGrant>(grant.Id, g => g.StatusId = StatusIds.AccessGrant.Revoked);

        var again = await z.RecordAsync(7m, 20m, clientOperationId: operationId, client: manager);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal("MSG-GEN-08", await again.ErrorCodeAsync());
        Assert.DoesNotContain(savedId.ToString(), await again.Content.ReadAsStringAsync());
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Export_right_without_view_is_refused()
    {
        var z = await api.ZoneAsync();
        await z.RecordedAsync(7m, 20m);
        var (exportOnly, _) = await api.StaffAsync(z.Zone, view: false, export: true);

        var response = await exportOnly.GetAsync($"{z.Base}/export");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Client_operation_id_is_scoped_to_one_account_and_one_zone()
    {
        var zoneA = await api.ZoneAsync();
        var zoneB = await api.ZoneAsync();
        var (manager, managerAccount) = await api.StaffAsync(zoneA.Zone, create: true);
        var operationId = Guid.NewGuid();

        var byOwner = await zoneA.RecordAsync(7m, 20m, clientOperationId: operationId);
        // Another account's device may generate the same id: it is a different operation.
        var byManager = await zoneA.RecordAsync(7.4m, 21m, clientOperationId: operationId, client: manager);
        // The same account reusing the id for another zone is a client error, not a re-send.
        var otherZone = await zoneA.Owner.PostAsJsonAsync(zoneB.Base, new
        {
            clientOperationId = operationId,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ph = 7m,
            salinity = 20m,
            confirm = true,
        });

        Assert.Equal(HttpStatusCode.Created, byOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Created, byManager.StatusCode);
        var managerBody = await byManager.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual((await byOwner.Content.ReadFromJsonAsync<JsonElement>()).Id(), managerBody.Id());
        Assert.Equal(managerAccount.Id, managerBody.GetProperty("measuredBy").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, otherZone.StatusCode);
        Assert.Equal("MSG-GEN-01", await otherZone.ErrorCodeAsync());
        Assert.Equal("Client operation id", (await otherZone.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(2, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == zoneA.Zone.Id)));
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == zoneB.Zone.Id)));
    }

    /// <summary>A staff account holding the deployment's default Accountant role at All zones (ACS-10 defaults).</summary>
    private async Task<HttpClient> AccountantAtAllZonesAsync()
    {
        var role = new Role { Name = $"Accountant {NewCode()}" };
        foreach (var permission in DefaultRoles.Accountant.Permissions)
        {
            var actions = DefaultRoles.ActionsOf(permission);
            role.Permissions.Add(new RolePermission
            {
                FunctionCode = permission.FunctionCode,
                CanView = actions.Contains(PermissionAction.View),
                CanCreate = actions.Contains(PermissionAction.Create),
                CanEdit = actions.Contains(PermissionAction.Edit),
                CanDelete = actions.Contains(PermissionAction.Delete),
                OtherPermissions = actions
                    .Where(a => a is not (PermissionAction.View or PermissionAction.Create or PermissionAction.Edit or PermissionAction.Delete))
                    .Select(a => a.ToString().ToUpperInvariant())
                    .ToList(),
            });
        }

        await api.UseDbAsync(db => db.Roles.Add(role));
        var account = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(account, role, zone: null);
        return await api.LoginAsync(account.Username);
    }

    private async Task<Role> AddWaterRoleAsync(bool view, bool create, bool edit, bool export)
    {
        var role = new Role { Name = $"Water all {NewCode()}" };
        role.Permissions.Add(new RolePermission
        {
            FunctionCode = PermissionFunctions.WaterMeasurements,
            CanView = view,
            CanCreate = create,
            CanEdit = edit,
            OtherPermissions = export ? ["EXPORT"] : [],
        });
        await api.UseDbAsync(db => db.Roles.Add(role));
        return role;
    }
}
