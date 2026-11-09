using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
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
            await owner.CreateItemAsync("Seed", "kg"), await owner.CreateItemAsync("Material", "piece"), await owner.CreateItemAsync("Other", "bag"));
    }

    /// <summary>Logged-in staff with one pair of the fixed <paramref name="role"/> in the zone (All zones when null).</summary>
    public static async Task<HttpClient> StaffAsync(this FarmingHost host, Guid? zoneId, StaffRole role)
    {
        using var scope = host.Api.Services.CreateScope();
        var staff = new Account
        {
            Username = NewCode(), DisplayName = "Purchasing staff", SubjectType = SubjectType.Staff, StatusId = StatusIds.Account.Active,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), PhoneNumber = NewPhone(), PhoneVerifiedAt = DateTimeOffset.UtcNow,
        };
        await host.UseDbAsync(db => db.Accounts.Add(staff));
        await host.Api.GrantAsync(staff, role, zoneId, host.Owner.Account.Id);
        return await host.LoginAsync(staff.Username);
    }

    public static Task<HttpClient> ZoneManagerAsync(this FarmingHost host, Guid zoneId) => host.StaffAsync(zoneId, StaffRole.ZoneManager);

    /// <summary>A material usage on the raft, dated <paramref name="date"/> (default today), recorded by the Owner.</summary>
    public static async Task<Guid> UseMaterialAsync(this FarmingHost host, Guid raftId, Guid itemId, decimal quantity, DateOnly? date = null)
    {
        var usage = new MaterialUsage
        {
            RaftId = raftId, ItemId = itemId, Quantity = quantity, UsageDate = date ?? PurchasingTestKit.Today, RepairReason = "Broken buoy",
            RecordedByAccountId = host.Owner.Account.Id,
        };
        await host.UseDbAsync(db => db.MaterialUsages.Add(usage));
        return usage.Id;
    }

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

    /// <summary>What a client does on 409 MSG06: the same request again. Each round has a winner, so the attempts are not reached.</summary>
    public static async Task<HttpResponseMessage> AgainOnConflictAsync(Func<Task<HttpResponseMessage>> request)
    {
        const int attempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            var response = await request();
            if (attempt == attempts || await response.CodeOrStatusAsync() != "409 MSG06")
            {
                return response;
            }
        }
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
