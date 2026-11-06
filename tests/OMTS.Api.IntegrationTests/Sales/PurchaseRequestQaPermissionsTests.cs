using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: what allZones on PURCHASE_REQUESTS says in /api/me/permissions, and that it opens nothing else (BR-FARM-08, PM 03/11).</summary>
public class PurchaseRequestQaPermissionsTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static List<JsonElement> Functions(JsonElement permissions) => permissions.GetProperty("functions").EnumerateArray().ToList();

    private static JsonElement Function(JsonElement permissions, string code) =>
        Functions(permissions).Single(f => f.Text("function") == code);

    // allZones is read by the menus. For a manager of zone A it says PURCHASE_REQUESTS View, and no other function changes.
    [Fact]
    public async Task All_zones_on_purchase_requests_widens_nothing_else_for_a_manager_of_one_zone()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var other = await factory.AddZoneAsync(NewCode());
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, StaffRole.ZoneManager, zone);
        var manager = await factory.LoginAsync(staff.Username);

        var permissions = await (await manager.GetAsync("/api/me/permissions")).ReadAsync(HttpStatusCode.OK);

        var row = Function(permissions, PermissionFunctions.PurchaseRequests);
        Assert.True(row.GetProperty("allZones").GetBoolean());
        Assert.Empty(row.GetProperty("zoneIds").EnumerateArray());
        Assert.Equal(["View"], row.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).ToList());

        // Every all-zones row is a function with no zone on its records; every other row stays in this one zone.
        foreach (var function in Functions(permissions))
        {
            var code = function.Text("function")!;
            Assert.Equal(PermissionFunctions.IsCooperativeWide(code), function.GetProperty("allZones").GetBoolean());
            if (!PermissionFunctions.IsCooperativeWide(code))
            {
                Assert.Equal([zone.Id], function.GetProperty("zoneIds").EnumerateArray().Select(z => z.GetGuid()).ToList());
            }
        }

        using var scope = factory.Services.CreateScope();
        var access = (await scope.ServiceProvider.GetRequiredService<PermissionService>().GetAccessAsync(staff.Id, CancellationToken.None))!;
        Assert.False(access.Allows(PermissionFunctions.PurchaseRequests, PermissionAction.Edit, PermissionScope.AnyZone, null));
        Assert.False(access.Allows(PermissionFunctions.PurchaseRequests, PermissionAction.Edit, PermissionScope.ZoneScoped, zone.Id));
        Assert.False(access.Allows(PermissionFunctions.CultivationLogs, PermissionAction.View, PermissionScope.ZoneScoped, other.Id));
        Assert.False(access.Allows(PermissionFunctions.SalesInvoices, PermissionAction.View, PermissionScope.ZoneScoped, other.Id));
        Assert.True(access.Allows(PermissionFunctions.SalesInvoices, PermissionAction.View, PermissionScope.ZoneScoped, zone.Id));
    }

    [Fact]
    public async Task An_accountant_has_no_row_for_purchase_requests_at_one_zone_or_at_all_zones()
    {
        foreach (var client in new[] { await factory.AccountantAsync(), await factory.AccountantAsync(await factory.AddZoneAsync(NewCode())) })
        {
            var permissions = await (await client.GetAsync("/api/me/permissions")).ReadAsync(HttpStatusCode.OK);
            Assert.DoesNotContain(Functions(permissions), f => f.Text("function") == PermissionFunctions.PurchaseRequests);
        }
    }
}
