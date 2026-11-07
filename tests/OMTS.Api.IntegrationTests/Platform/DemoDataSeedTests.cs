using System.Net;
using System.Net.Http.Json;
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
        ("CooperativeCertifications", db => db.CooperativeCertifications.CountAsync()),
        ("Accounts", db => db.Accounts.CountAsync()),
        ("AccessGrants", db => db.AccessGrants.CountAsync()),
        ("Customers", db => db.Customers.CountAsync()),
        ("Products", db => db.Products.CountAsync()),
        ("Employees", db => db.Employees.CountAsync()),
        ("FarmingZones", db => db.FarmingZones.CountAsync()),
        ("ZoneComplianceDocuments", db => db.ZoneComplianceDocuments.CountAsync()),
        ("Rafts", db => db.Rafts.CountAsync()),
        ("Lanes", db => db.Lanes.CountAsync()),
        ("CultivationCycles", db => db.CultivationCycles.CountAsync()),
        ("CultivationLogs", db => db.CultivationLogs.CountAsync()),
        ("CultivationLogPhotos", db => db.CultivationLogPhotos.CountAsync()),
        ("Suppliers", db => db.Suppliers.CountAsync()),
        ("FarmingInputItems", db => db.FarmingInputItems.CountAsync()),
        ("PurchaseReceipts", db => db.PurchaseReceipts.CountAsync()),
        ("PurchaseReceiptLines", db => db.PurchaseReceiptLines.CountAsync()),
        ("SupplierPayments", db => db.SupplierPayments.CountAsync()),
        ("PurchaseReturns", db => db.PurchaseReturns.CountAsync()),
        ("WaterMeasurements", db => db.WaterMeasurements.CountAsync()),
        ("JobTypes", db => db.JobTypes.CountAsync()),
        ("PayRates", db => db.PayRates.CountAsync()),
        ("EmployeeJobTypes", db => db.EmployeeJobTypes.CountAsync()),
        ("AttendanceSettingsVersions", db => db.AttendanceSettingsVersions.CountAsync()),
        ("Payrolls", db => db.Payrolls.CountAsync()),
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

    // Outside Development the old Bootstrap:SeedDemoData switch reopens nothing; only Staging has a flag of its own
    // (DemoSeedGateTests).
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Demo_flag_is_refused_outside_development_and_creates_nothing(string environment)
    {
        await using var host = BootstrapTests.WithSecrets(new AuthApiFactory())
            .WithWebHostBuilder(b => b.UseEnvironment(environment).UseSetting("Bootstrap:SeedDemoData", "true"));

        var refused = await RunAsync(host);

        Assert.Equal(DeploymentBootstrapper.Failure, refused.ExitCode);
        Assert.Contains(refused.Messages, m => m.Contains("refused"));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        Assert.False(await db.Cooperatives.AnyAsync());
        Assert.False(await db.FarmingZones.AnyAsync());
    }

    private static async Task AssertDemoDataAsync(SqlServerApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();

        // FARM-01: the SRS 27/10 profile fields, a logo and a cover photo, and one Valid certification with its file.
        var cooperative = await db.Cooperatives.AsNoTracking().SingleAsync();
        Assert.Equal(("HTX Hàu Vân Đồn", 2012), (cooperative.ShortName, cooperative.YearEstablished));
        Assert.All(
            new[] { cooperative.TaxCode, cooperative.SocialLink, cooperative.IntroductionVi, cooperative.IntroductionEn, cooperative.LogoUrl, cooperative.CoverPhotoUrl },
            value => Assert.False(string.IsNullOrEmpty(value)));
        // Two certifications with their files: one Valid and one Expiring soon (GFR-03 reminds it).
        var certifications = await db.CooperativeCertifications.AsNoTracking().OrderBy(c => c.ExpiryDate).ToListAsync();
        Assert.Equal(2, certifications.Count);
        Assert.All(certifications, c => Assert.Equal(cooperative.Id, c.CooperativeId));
        Assert.All(certifications, c => Assert.NotNull(c.FileUrl));
        var businessToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        Assert.InRange(certifications[0].ExpiryDate!.Value, businessToday, businessToday.AddDays(30));
        Assert.True(certifications[1].ExpiryDate > businessToday.AddDays(30));

        Assert.Equal(3, await db.FarmingZones.CountAsync());
        Assert.Equal(1, await db.FarmingZones.CountAsync(z => z.StatusId == StatusIds.Usage.Discontinued));
        // One zone per license: DEMO-01 an expired license replaced by one expiring soon, DEMO-02 a valid one and an older expired one.
        Assert.Equal(4, await db.ZoneComplianceDocuments.CountAsync());
        Assert.Equal(1, await db.ZoneComplianceDocuments.CountAsync(d => d.ReplacesDocumentId != null));
        Assert.Equal(2, await db.ZoneComplianceDocuments.Select(d => d.ZoneId).Distinct().CountAsync());
        Assert.Equal(20, await db.Rafts.CountAsync());
        // First raft of each In Use zone: two photos and a Removed lane; second: one photo; third: no map pin (FARM-04).
        Assert.Equal(6, await db.RaftPhotos.CountAsync());
        Assert.Equal(2, await db.Lanes.CountAsync(l => l.StatusId == StatusIds.Lane.Removed));
        Assert.Equal(2, await db.Rafts.CountAsync(r => r.Latitude == null && r.Longitude == null));
        // CYCLE-01: five Open cycles per In Use zone, started weeks ago, for the later PRs and the GFR-04 reminder.
        var cycles = await db.CultivationCycles.AsNoTracking().Include(c => c.Raft).ToListAsync();
        Assert.Equal(10, cycles.Count);
        Assert.All(cycles, c => Assert.Equal(StatusIds.CultivationCycle.Open, c.StatusId));
        Assert.All(cycles, c => Assert.Equal(StatusIds.Usage.InUse, c.Raft.StatusId));
        Assert.All(cycles, c => Assert.True(c.StartDate < businessToday.AddDays(-30)));
        Assert.Equal(6, cycles.Count(c => c.ExpectedHarvestDate is not null));
        Assert.Equal(9, await db.Employees.CountAsync());
        // Everyone has a type (BR-LABOR-01): those with a login are Staff, the others seasonal workers; one has no phone number.
        Assert.Equal(9, await db.EmployeeTypePeriods.Select(p => p.EmployeeId).Distinct().CountAsync());
        Assert.Equal(3, await db.EmployeeTypePeriods.CountAsync(p => p.Type == OMTS.Domain.Enums.EmployeeType.SeasonalWorker));
        Assert.Equal(1, await db.Employees.CountAsync(e => e.PhoneNumber == null));
        Assert.True(await db.Employees.Where(e => e.BankAccount != null).AllAsync(e => e.BankAccount!.AccountNumber.StartsWith("9999")));
        Assert.Equal(1, await db.Employees.CountAsync(e => e.StatusId == StatusIds.Employee.Left));
        Assert.Equal(5, await db.Suppliers.CountAsync());
        Assert.Equal(15, await db.FarmingInputItems.CountAsync());
        // SALE-11: a price list with one Discontinued product to resume. SALE-04: the two trader profiles and five offline
        // ones, every offline number checked, the last two the same buyer to merge.
        Assert.Equal(4, await db.Products.CountAsync());
        Assert.Equal(1, await db.Products.CountAsync(p => p.StatusId == StatusIds.Usage.Discontinued));
        Assert.All(await db.Products.ToListAsync(), p => Assert.True(p.UnitPrice > 0));
        Assert.Equal(7, await db.Customers.CountAsync());
        Assert.Equal(2, await db.Customers.CountAsync(c => c.AccountId != null));
        Assert.Equal(5, await db.Customers.CountAsync(c => c.AccountId == null && c.PhoneCheckedByStaff));

        var statuses = await db.PurchaseReceipts.GroupBy(r => r.StatusId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        Assert.Equal(20, statuses.Sum(s => s.Count));
        Assert.Equal(2, statuses.Single(s => s.Key == StatusIds.PurchaseReceipt.Draft).Count);
        Assert.Equal(2, statuses.Single(s => s.Key == StatusIds.PurchaseReceipt.Cancelled).Count);
        // One return stands and one was cancelled by the Owner (PURCHASE-02.1), both with TH codes.
        Assert.Equal(1, await db.PurchaseReturns.CountAsync(r => r.StatusId == StatusIds.PurchaseReturn.Recorded));
        Assert.Equal(1, await db.PurchaseReturns.CountAsync(r => r.StatusId == StatusIds.PurchaseReturn.Cancelled && r.CancelReason != null));
        Assert.True(await db.PurchaseReturns.AllAsync(r => r.Code.StartsWith("TH")));
        // TT codes; payments are not linked to receipts; a supplier paid back and a payment was cancelled (PURCHASE-05.2/05.3).
        Assert.True(await db.SupplierPayments.AnyAsync(p => p.StatusId == StatusIds.SupplierPayment.Recorded && p.Kind == SupplierPaymentKind.Payment));
        Assert.True(await db.SupplierPayments.AnyAsync(p => p.StatusId == StatusIds.SupplierPayment.Recorded && p.Kind == SupplierPaymentKind.PaidBack));
        Assert.Equal(1, await db.SupplierPayments.CountAsync(p => p.StatusId == StatusIds.SupplierPayment.Cancelled));
        Assert.True(await db.SupplierPayments.AllAsync(p => p.Code.StartsWith("TT")));
        // Each transaction told the Owner (MSG-PURCHASE-04), the cancellation apart from its payment.
        var payments = await db.SupplierPayments.CountAsync();
        Assert.Equal(payments + 1, await db.Notifications.CountAsync(n => n.Type == NotificationTypes.SupplierTransaction));
        // PN codes; Material lines name their raft, seed lines may; one amount keeps its dongs (BR-PURCHASE-02).
        Assert.True(await db.PurchaseReceipts.AllAsync(r => r.Code.StartsWith("PN")));
        Assert.True(await db.PurchaseReceiptLines.AnyAsync(l => l.RaftId != null && l.Item!.Group == InputItemGroup.Seed));
        Assert.True(await db.PurchaseReceiptLines.Where(l => l.Item!.Group == InputItemGroup.Material && l.Receipt.StatusId != StatusIds.PurchaseReceipt.Draft).AllAsync(l => l.RaftId != null));
        Assert.True(await db.PurchaseReceiptLines.AnyAsync(l => l.LineAmount % 1000 != 0));
        // The Owner was told of each completed receipt (MSG-PURCHASE-01), cancelled ones included.
        Assert.Equal(18, await db.Notifications.Where(n => n.Type == NotificationTypes.PurchaseReceiptCompleted).Select(n => n.RelatedRecordId).Distinct().CountAsync());

        // Dates are relative to today in UTC+7, whenever the seed runs.
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);
        Assert.True(await db.ZoneComplianceDocuments.AnyAsync(d => d.ExpiryDate > today && d.ExpiryDate <= today.AddDays(30)));
        Assert.True(await db.ZoneComplianceDocuments.AnyAsync(d => d.ExpiryDate < today));
        Assert.True(await db.PurchaseReceipts.AllAsync(r => r.ReceiptDate <= today && r.ReceiptDate >= today.AddDays(-90)));

        await AssertWaterDataAsync(db, today);
        await AssertLaborSettingsAsync(db, today);
    }

    // LABOR-11, LABOR-15, LABOR-16 (OMTS-227): job types with rates, one discontinued, the seasonal workers' profiles naming
    // them, two versions of the settings, and last month locked. A rerun moves none of it, whichever day it runs.
    private static async Task AssertLaborSettingsAsync(OmtsDbContext db, DateOnly today)
    {
        Assert.Equal(4, await db.JobTypes.CountAsync());
        Assert.Equal(1, await db.JobTypes.CountAsync(j => j.StatusId == StatusIds.Usage.Discontinued));
        Assert.True(await db.JobTypes.AllAsync(j => j.Rates.Any()));
        Assert.Equal(6, await db.PayRates.CountAsync());
        Assert.Equal(4, await db.EmployeeJobTypes.CountAsync());
        var seasonal = await db.EmployeeTypePeriods.Where(p => p.Type == OMTS.Domain.Enums.EmployeeType.SeasonalWorker).Select(p => p.EmployeeId).ToListAsync();
        Assert.True(await db.EmployeeJobTypes.AllAsync(j => seasonal.Contains(j.EmployeeId)));
        Assert.Equal(2, await db.AttendanceSettingsVersions.CountAsync());

        var cooperative = await db.Cooperatives.SingleAsync();
        Assert.Equal((1, false), (cooperative.PayPeriodStartDay, cooperative.AutomaticPayroll));

        var payrolls = await db.Payrolls.OrderBy(p => p.PeriodStart).ToListAsync();
        Assert.Equal([StatusIds.Payroll.Finalized, StatusIds.Payroll.AwaitingFinalization], payrolls.Select(p => p.StatusId));
        Assert.Equal(payrolls[0].PeriodEnd.AddDays(1), payrolls[1].PeriodStart);
        // The test may run just after midnight of the first of a month, later than the seed: last month is then the one before.
        Assert.InRange(payrolls[1].PeriodEnd, today.AddMonths(-1).AddDays(-1), today);
        Assert.True(payrolls.All(p => p.PeriodStart.Day == 1));
        Assert.True(await db.PayRates.Where(r => r.HourlyRate == 36_000).AllAsync(r => r.EffectiveFrom > payrolls[1].PeriodEnd));
    }

    // WATER (OMTS-219): thresholds and 30 days of readings per In Use zone, at its rafts, each with a temperature and a photo:
    // three outside the thresholds, a mistyped salinity corrected in place and one voided reading.
    private static async Task AssertWaterDataAsync(OmtsDbContext db, DateOnly today)
    {
        var zones = await db.FarmingZones.Where(z => z.StatusId == StatusIds.Usage.InUse).ToListAsync();
        Assert.Equal(2, zones.Count);
        Assert.All(zones, z => Assert.Equal((7.5m, 8.5m, 18m, 32m), (z.PhMin!.Value, z.PhMax!.Value, z.SalinityMin!.Value, z.SalinityMax!.Value)));

        var water = await db.WaterMeasurements.AsNoTracking().ToListAsync();
        Assert.Equal(60, water.Count);
        Assert.Equal(59, water.Count(m => m.StatusId == StatusIds.WaterMeasurement.Active));
        Assert.DoesNotContain(water, m => m.Salinity == 2.6m);
        var correctedId = await db.AuditEvents.Where(e => e.Action == "WaterMeasurement.Corrected").Select(e => e.RecordId).SingleAsync();
        Assert.Single(water, m => m.Id == correctedId && m.Salinity == 26.0m && m.StatusId == StatusIds.WaterMeasurement.Active && !m.ThresholdExceeded && m.AlertedAt is null);
        Assert.Single(water, m => m.StatusId == StatusIds.WaterMeasurement.Voided && m.VoidReason is { Length: > 0 });
        Assert.Equal(3, water.Count(m => m.StatusId == StatusIds.WaterMeasurement.Active && m.ThresholdExceeded));
        Assert.All(water, m => Assert.Equal((7.5m, 15m, 32m), (m.PhMinUsed!.Value, m.TemperatureMinUsed!.Value, m.TemperatureMaxUsed!.Value)));
        Assert.All(water, m => Assert.InRange(m.Temperature, 15m, 32m));
        Assert.Equal(60, water.Select(m => m.PhotoUrl).Distinct().Count());
        var rafts = await db.Rafts.AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.All(water, m => Assert.Equal((m.ZoneId, StatusIds.Usage.InUse), (rafts[m.RaftId].ZoneId, rafts[m.RaftId].StatusId)));
        Assert.All(water.GroupBy(m => m.ZoneId), zone => Assert.Equal(8, zone.Select(m => m.RaftId).Distinct().Count()));
        foreach (var zone in zones)
        {
            var days = water.Where(m => m.ZoneId == zone.Id)
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

        // Locked trader, and staff whose only pair is revoked: refused with MSG-ACCOUNT-07 after the password is proven. The
        // message does not say why, so the data shows each account is refused for its own reason.
        foreach (var username in new[] { "thuonglai2", "nhanvien.hethan" })
        {
            var (status, body) = await LoginAsync(username);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("MSG-ACCOUNT-07", body.GetProperty("code").GetString());
        }

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var locked = await db.Accounts.SingleAsync(a => a.Username == "thuonglai2");
        Assert.Equal(StatusIds.Account.Locked, locked.StatusId);
        var revoked = await db.Accounts.SingleAsync(a => a.Username == "nhanvien.hethan");
        Assert.Equal(StatusIds.Account.Active, revoked.StatusId);
        var grants = await db.AccessGrants.Where(g => g.AccountId == revoked.Id).ToListAsync();
        Assert.NotEmpty(grants);
        // Its only pair is Revoked with a reason (FARM-05.1), kept in the history of the account.
        Assert.All(grants, g =>
        {
            Assert.Equal(StatusIds.AccessGrant.Revoked, g.StatusId);
            Assert.NotNull(g.RevokedAt);
            Assert.False(string.IsNullOrWhiteSpace(g.RevokeReason));
        });
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
