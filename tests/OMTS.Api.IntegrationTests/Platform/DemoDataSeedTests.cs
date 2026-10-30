using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>The Development demo data of <c>seed --bootstrap --demo</c> (OMTS-213).</summary>
public class DemoDataSeedTests
{
    private const string DemoPassword = "Demo@1234";
    private const string OwnerTemporaryPassword = "Temp2026ab";

    // Every table the demo seed writes to.
    private static readonly (string Table, Func<OmtsDbContext, Task<int>> Count)[] SeededTables =
    [
        ("Cooperatives", db => db.Cooperatives.CountAsync()),
        ("Accounts", db => db.Accounts.CountAsync()),
        ("AccessGrants", db => db.AccessGrants.CountAsync()),
        ("Customers", db => db.Customers.CountAsync()),
        ("Employees", db => db.Employees.CountAsync()),
        ("FarmingZones", db => db.FarmingZones.CountAsync()),
        ("ZoneComplianceDocuments", db => db.ZoneComplianceDocuments.CountAsync()),
        ("Rafts", db => db.Rafts.CountAsync()),
        ("Lanes", db => db.Lanes.CountAsync()),
        ("Suppliers", db => db.Suppliers.CountAsync()),
        ("FarmingInputItems", db => db.FarmingInputItems.CountAsync()),
        ("PurchaseReceipts", db => db.PurchaseReceipts.CountAsync()),
        ("PurchaseReceiptLines", db => db.PurchaseReceiptLines.CountAsync()),
        ("SeedReceiptLots", db => db.SeedReceiptLots.CountAsync()),
        ("SupplierPayments", db => db.SupplierPayments.CountAsync()),
        ("PurchaseReturns", db => db.PurchaseReturns.CountAsync()),
        ("WaterMeasurements", db => db.WaterMeasurements.CountAsync()),
        ("Notifications", db => db.Notifications.CountAsync()),
        ("AuditEvents", db => db.AuditEvents.CountAsync()),
    ];

    [SqlServerFact]
    public async Task Second_demo_run_on_sql_server_changes_no_row_counts_and_every_login_lands_in_its_branch()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã Nuôi hàu Vân Đồn",
                [BootstrapSettings.OwnerDisplayNameKey] = "Nguyễn Văn Hải",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = OwnerTemporaryPassword,
                ["Storage:Local:RootPath"] = storageRoot,
            });

            var first = await RunAsync(api);
            var afterFirst = await CountsAsync(api);
            var second = await RunAsync(api);
            var afterSecond = await CountsAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, first.ExitCode);
            Assert.Equal(DeploymentBootstrapper.Success, second.ExitCode);
            Assert.Equal(afterFirst, afterSecond);
            Assert.All(second.Messages.Where(m => m.StartsWith("Demo ")), m => Assert.EndsWith("already there.", m));
            Assert.DoesNotContain(first.Messages.Concat(second.Messages), m => m.Contains(DemoPassword) || m.Contains(OwnerTemporaryPassword));

            await AssertDemoDataAsync(api);
            await AssertLoginBranchesAsync(api);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    // Any environment but Development, and no setting reopens it (the old Bootstrap:SeedDemoData switch is gone).
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Demo_flag_is_refused_outside_development_and_creates_nothing(string environment)
    {
        await using var host = BootstrapTests.WithSecrets(new AuthApiFactory())
            .WithWebHostBuilder(b => b.UseEnvironment(environment).UseSetting("Bootstrap:SeedDemoData", "true"));

        var refused = await RunAsync(host);

        Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
        Assert.Contains(refused.Messages, m => m.Contains("refused outside Development"));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        Assert.False(await db.Cooperatives.AnyAsync());
        Assert.False(await db.FarmingZones.AnyAsync());
    }

    private static async Task AssertDemoDataAsync(SqlServerApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();

        Assert.Equal(3, await db.FarmingZones.CountAsync());
        Assert.Equal(1, await db.FarmingZones.CountAsync(z => z.StatusId == StatusIds.Usage.Discontinued));
        Assert.Equal(6, await db.ZoneComplianceDocuments.CountAsync());
        Assert.Equal(20, await db.Rafts.CountAsync());
        Assert.Equal(8, await db.Employees.CountAsync());
        Assert.Equal(1, await db.Employees.CountAsync(e => e.StatusId == StatusIds.Employee.Left));
        Assert.Equal(5, await db.Suppliers.CountAsync());
        Assert.Equal(15, await db.FarmingInputItems.CountAsync());

        var statuses = await db.PurchaseReceipts.GroupBy(r => r.StatusId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        Assert.Equal(20, statuses.Sum(s => s.Count));
        Assert.Equal(2, statuses.Single(s => s.Key == StatusIds.PurchaseReceipt.Draft).Count);
        Assert.Equal(2, statuses.Single(s => s.Key == StatusIds.PurchaseReceipt.Cancelled).Count);
        Assert.Equal(1, await db.PurchaseReturns.CountAsync());
        Assert.True(await db.SupplierPayments.AnyAsync(p => p.StatusId == StatusIds.SupplierPayment.Recorded));
        Assert.True(await db.PurchaseReceipts.AnyAsync(r => r.DiscountAmount > 0));

        // Dates are relative to today in UTC+7, whenever the seed runs.
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);
        Assert.True(await db.ZoneComplianceDocuments.AnyAsync(d => d.ExpiryDate > today && d.ExpiryDate <= today.AddDays(30)));
        Assert.True(await db.ZoneComplianceDocuments.AnyAsync(d => d.ExpiryDate < today));
        Assert.True(await db.PurchaseReceipts.AllAsync(r => r.ReceiptDate <= today && r.ReceiptDate >= today.AddDays(-90)));

        await AssertWaterDataAsync(db, today);
    }

    // ENV (OMTS-214): thresholds and 30 days of readings per In Use zone, three outside the thresholds plus a mistyped
    // salinity that was corrected (both versions kept), and one voided reading.
    private static async Task AssertWaterDataAsync(OmtsDbContext db, DateOnly today)
    {
        var zones = await db.FarmingZones.Where(z => z.StatusId == StatusIds.Usage.InUse).ToListAsync();
        Assert.Equal(2, zones.Count);
        Assert.All(zones, z => Assert.Equal((7.5m, 8.5m, 18m, 32m), (z.PhMin!.Value, z.PhMax!.Value, z.SalinityMin!.Value, z.SalinityMax!.Value)));

        var water = await db.WaterMeasurements.AsNoTracking().ToListAsync();
        Assert.Equal(61, water.Count);
        Assert.Equal(59, water.Count(m => m.StatusId == StatusIds.WaterMeasurement.Confirmed));
        Assert.Single(water, m => m.StatusId == StatusIds.WaterMeasurement.Superseded && m.ThresholdExceeded && m.Salinity == 2.6m);
        Assert.Single(water, m => m.CorrectedFromMeasurementId is not null && m.StatusId == StatusIds.WaterMeasurement.Confirmed && !m.ThresholdExceeded);
        Assert.Single(water, m => m.StatusId == StatusIds.WaterMeasurement.Voided && m.VoidReason is { Length: > 0 });
        Assert.Equal(3, water.Count(m => m.StatusId == StatusIds.WaterMeasurement.Confirmed && m.ThresholdExceeded));
        Assert.All(water, m => Assert.Equal(7.5m, m.PhMinUsed));
        foreach (var zone in zones)
        {
            var days = water.Where(m => m.ZoneId == zone.Id && m.CorrectedFromMeasurementId is null)
                .Select(m => BusinessClock.ToBusinessDate(m.MeasuredAt)).ToList();
            Assert.Equal(30, days.Distinct().Count());
            Assert.All(days, d => Assert.InRange(d, today.AddDays(-30), today.AddDays(-1)));
        }

        Assert.True(await db.Notifications.AnyAsync(n => n.Type == NotificationTypes.WaterAlert));
    }

    // Each account must really land in the login branch the demo data promises.
    private static async Task AssertLoginBranchesAsync(SqlServerApiFactory api)
    {
        var client = api.CreateClient();
        async Task<(HttpStatusCode Status, JsonElement Body)> LoginAsync(string username)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier = username, password = DemoPassword });
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }

        foreach (var username in new[] { "owner", "quanlyvung1", "quanlyvung2", "ketoan", "thuonglai1" })
        {
            var (status, body) = await LoginAsync(username);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(body.GetProperty("mustChangePassword").GetBoolean(), username);
            Assert.False(body.GetProperty("phoneVerificationRequired").GetBoolean(), username);
        }

        var mustChange = await LoginAsync("nhanvien.doimk");
        Assert.Equal(HttpStatusCode.OK, mustChange.Status);
        Assert.True(mustChange.Body.GetProperty("mustChangePassword").GetBoolean());

        var unverified = await LoginAsync("nhanvien.chuaxacthuc");
        Assert.Equal(HttpStatusCode.OK, unverified.Status);
        Assert.True(unverified.Body.GetProperty("phoneVerificationRequired").GetBoolean());

        // Locked trader, and staff whose only grant has expired: refused with MSG13 after the password is proven. The
        // message does not say why, so the data shows each account is refused for its own reason.
        foreach (var username in new[] { "thuonglai2", "nhanvien.hethan" })
        {
            var (status, body) = await LoginAsync(username);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("MSG13", body.GetProperty("code").GetString());
        }

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var locked = await db.Accounts.SingleAsync(a => a.Username == "thuonglai2");
        Assert.Equal(StatusIds.Account.Locked, locked.StatusId);
        var expired = await db.Accounts.SingleAsync(a => a.Username == "nhanvien.hethan");
        Assert.Equal(StatusIds.Account.Active, expired.StatusId);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);
        var grants = await db.AccessGrants.Where(g => g.AccountId == expired.Id).ToListAsync();
        Assert.NotEmpty(grants);
        Assert.All(grants, g => Assert.True(g.EffectiveTo < today));
    }

    private static async Task<IReadOnlyDictionary<string, int>> CountsAsync(SqlServerApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var counts = new Dictionary<string, int>();
        foreach (var (table, count) in SeededTables)
        {
            counts[table] = await count(db);
        }

        return counts;
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(seedDemoData: true, CancellationToken.None);
    }
}
