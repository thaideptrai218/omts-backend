using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using FarmingKit = OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// INP set-up on a real, migrated SQL Server database (FARM host): a zone with a raft made through the FARM API,
/// a supplier and one item per group made through the INP API. For <see cref="SqlServerFactAttribute"/> tests only.
/// </summary>
public static class PurchasingSqlServerKit
{
    public static async Task<PurchasingWorld> WorldAsync(this FarmingHost host)
    {
        var owner = host.Owner.Client;
        var zoneId = (await FarmingKit.CreateZoneAsync(owner)).GetProperty("id").GetGuid();
        var raftId = (await FarmingKit.CreateRaftAsync(owner, zoneId)).GetProperty("id").GetGuid();
        var zone = await host.QueryAsync(db => db.FarmingZones.SingleAsync(z => z.Id == zoneId));
        var raft = await host.QueryAsync(db => db.Rafts.SingleAsync(r => r.Id == raftId));
        return new PurchasingWorld(
            owner, zone, raft, await owner.CreateSupplierAsync(),
            await owner.CreateItemAsync("Seed", "kg"), await owner.CreateItemAsync("RaftMaterial", "piece"), await owner.CreateItemAsync("Other", "bag"));
    }

    /// <summary>Logged-in staff with one grant in the zone (All zones when null) of a role holding the permissions.</summary>
    public static async Task<HttpClient> StaffAsync(this FarmingHost host, Guid? zoneId, params Perm[] permissions)
    {
        using var scope = host.Api.Services.CreateScope();
        var staff = new Account
        {
            Username = NewCode(), DisplayName = "Purchasing staff", SubjectType = SubjectType.Staff, StatusId = StatusIds.Account.Active,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), PhoneNumber = NewPhone(), PhoneVerifiedAt = DateTimeOffset.UtcNow,
        };
        var role = new Role { Name = NewCode() };
        foreach (var p in permissions)
        {
            role.Permissions.Add(new RolePermission
            {
                FunctionCode = p.Function, CanView = p.View, CanCreate = p.Create, CanEdit = p.Edit, CanDelete = p.Delete, OtherPermissions = p.Export ? ["EXPORT"] : [],
            });
        }

        var grant = new AccessGrant
        {
            AccountId = staff.Id, RoleId = role.Id, ZoneId = zoneId, ScopeType = zoneId is null ? AccessScopeType.AllZones : AccessScopeType.SpecificZone,
            EffectiveFrom = PurchasingTestKit.Today.AddDays(-1), GrantedByAccountId = host.Owner.Account.Id,
        };
        await host.UseDbAsync(db =>
        {
            db.Accounts.Add(staff);
            db.Roles.Add(role);
            db.AccessGrants.Add(grant);
        });
        return await host.LoginAsync(staff.Username);
    }

    public static Task<HttpClient> ZoneManagerAsync(this FarmingHost host, Guid zoneId) =>
        host.StaffAsync(zoneId, Perm.Full(PermissionFunctions.PurchaseReceipts), new Perm(PermissionFunctions.Suppliers, true, true, true));

    /// <summary>Starts the requests together (HttpClient is safe for concurrent requests).</summary>
    public static async Task<HttpResponseMessage[]> InParallelAsync(int count, Func<int, Task<HttpResponseMessage>> request)
    {
        using var start = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await start.WaitAsync();
            return await request(i);
        }).ToList();
        start.Release(count);
        return await Task.WhenAll(tasks);
    }

    public static async Task<string?> CodeOrStatusAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            return JsonDocument.Parse(text).RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? $"{(int)response.StatusCode} {code.GetString()}"
                : $"{(int)response.StatusCode}";
        }
        catch (JsonException)
        {
            return $"{(int)response.StatusCode}";
        }
    }
}
