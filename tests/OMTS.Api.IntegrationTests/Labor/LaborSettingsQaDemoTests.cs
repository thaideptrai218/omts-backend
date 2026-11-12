using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsQaKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>QA of OMTS-227, the demo data of the client list (section 8) and its reruns, on SQL Server with a moving clock.</summary>
public class LaborSettingsQaDemoTests
{
    internal sealed class MovedClock : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }

    internal sealed class Host : IAsyncDisposable
    {
        private readonly SqlServerTestDatabase _database = SqlServerTestDatabase.Create();
        private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"omts-labor-demo-{Guid.NewGuid():N}");
        private readonly SqlServerApiFactory _api;

        public Host()
        {
            _api = new SqlServerApiFactory(_database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã Nuôi hàu Vân Đồn",
                [BootstrapSettings.OwnerDisplayNameKey] = "Nguyễn Văn Hải",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = AcsTestKit.NewPhone(),
                [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
                ["Storage:Local:RootPath"] = _storageRoot,
            });
            App = _api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            }));
        }

        public MovedClock Clock { get; } = new();

        public WebApplicationFactory<Program> App { get; }

        public async Task<BootstrapOutcome> RunAsync()
        {
            using var scope = App.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
        }

        public async Task<T> QueryAsync<T>(Func<OmtsDbContext, Task<T>> query)
        {
            using var scope = App.Services.CreateScope();
            return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            await _api.DisposeAsync();
            await _database.DisposeAsync();
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
    }

    internal static async Task<string> SnapshotAsync(Host host) => await host.QueryAsync(async db =>
    {
        var jobTypes = await db.JobTypes.OrderBy(j => j.Name).Select(j => new { j.Name, j.StatusId, j.UpdatedAt }).ToListAsync();
        var rates = await db.PayRates.OrderBy(r => r.EffectiveFrom).ThenBy(r => r.HourlyRate).Select(r => new { r.HourlyRate, r.EffectiveFrom, r.UpdatedAt }).ToListAsync();
        var settings = await db.AttendanceSettingsVersions.OrderBy(v => v.EffectiveFrom).Select(v => new { v.EffectiveFrom, v.BreakStart, v.BreakEnd, v.RoundingUnit, v.UpdatedAt }).ToListAsync();
        var payrolls = await db.Payrolls.OrderBy(p => p.PeriodStart).Select(p => new { p.PeriodStart, p.PeriodEnd, p.StatusId, p.UpdatedAt }).ToListAsync();
        var links = await db.EmployeeJobTypes.CountAsync();
        var audits = await db.AuditEvents.CountAsync();
        var cooperative = await db.Cooperatives.Select(c => new { c.PayPeriodStartDay, c.AutomaticPayroll }).SingleAsync();
        return JsonSerializer.Serialize(new { jobTypes, rates, settings, payrolls, links, audits, cooperative });
    });

    [SqlServerFact]
    public async Task The_demo_is_what_the_client_list_says_and_reruns_later_add_and_move_nothing()
    {
        await using var host = new Host();

        var first = await host.RunAsync();
        Assert.Equal(DeploymentBootstrapper.Success, first.ExitCode);
        var snapshot = await SnapshotAsync(host);
        var today = host.Clock.Today();
        var thisMonth = new DateOnly(today.Year, today.Month, 1);

        var rates = await host.QueryAsync(db => db.PayRates.Select(r => new { r.JobType.Name, r.HourlyRate, r.EffectiveFrom }).ToListAsync());
        Assert.Equal(
            [
                ("Thu hoạch", 38_000m, thisMonth.AddMonths(-6)), ("Thu hoạch", 40_000m, thisMonth.AddMonths(-3)),
                ("Treo giống", 35_000m, thisMonth.AddMonths(-6)), ("Treo giống", 36_000m, thisMonth.AddMonths(1)),
                ("Vệ sinh dây giàn", 30_000m, thisMonth.AddMonths(-6)),
                ("Đóng gói tại bè", 28_000m, thisMonth.AddMonths(-6)),
            ],
            rates.Select(r => (r.Name, r.HourlyRate, r.EffectiveFrom)).OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.EffectiveFrom));
        Assert.Equal("Đóng gói tại bè", await host.QueryAsync(db => db.JobTypes.Where(j => j.StatusId == StatusIds.Usage.Discontinued).Select(j => j.Name).SingleAsync()));
        var settings = await host.QueryAsync(db => db.AttendanceSettingsVersions.OrderBy(v => v.EffectiveFrom).ToListAsync());
        Assert.Equal(
            [(thisMonth.AddMonths(-6), AttendanceRoundingUnit.Minutes30), (thisMonth.AddMonths(-3), AttendanceRoundingUnit.Minutes15)],
            settings.Select(v => (v.EffectiveFrom, v.RoundingUnit)));
        Assert.All(settings, v => Assert.Equal((new TimeOnly(11, 30), new TimeOnly(12, 30)), (v.BreakStart!.Value, v.BreakEnd!.Value)));
        var payrolls = await host.QueryAsync(db => db.Payrolls.OrderBy(p => p.PeriodStart).ToListAsync());
        Assert.Equal(
            [
                (thisMonth.AddMonths(-2), thisMonth.AddMonths(-1).AddDays(-1), StatusIds.Payroll.Finalized),
                (thisMonth.AddMonths(-1), thisMonth.AddDays(-1), StatusIds.Payroll.Provisional),
            ],
            payrolls.Select(p => (p.PeriodStart, p.PeriodEnd, p.StatusId)));

        foreach (var days in new[] { 1, 45 })
        {
            host.Clock.Shift = TimeSpan.FromDays(days);

            var later = await host.RunAsync();

            Assert.Equal(DeploymentBootstrapper.Success, later.ExitCode);
            Assert.Equal(snapshot, await SnapshotAsync(host));
        }
    }

    [SqlServerFact]
    public async Task The_lock_of_the_demo_answers_as_the_client_list_says_for_the_demo_accountant_role()
    {
        await using var host = new Host();
        Assert.Equal(DeploymentBootstrapper.Success, (await host.RunAsync()).ExitCode);
        var today = host.Clock.Today();
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        Assert.True(await host.QueryAsync(db => db.AccessGrants.AnyAsync(g => g.Account.Username == "ketoan" && g.StatusId == StatusIds.AccessGrant.Effective)));
        var (accountant, _) = await LoginNewAsync(host.App, StaffRole.Accountant);
        var jobTypes = await accountant.GetFromJsonAsync<JsonElement>(JobTypes);
        var harvesting = jobTypes.GetProperty("items").EnumerateArray().Single(j => j.Str("name") == "Thu hoạch");

        // The month before last is Finalized and locked; last month is Provisional, which locks nothing (BR-LABOR-13).
        var lockedEnd = thisMonth.AddMonths(-1).AddDays(-1).ToString("yyyy-MM-dd");
        var refused = await accountant.PostRateAsync(harvesting, 45_000, lockedEnd);
        var saved = await accountant.PostRateAsync(harvesting, 45_000, thisMonth.ToString("yyyy-MM-dd"));

        Assert.Equal(lockedEnd, (await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null)).Str("lockedUntil"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    // "reruns add nothing": a rerun after real use. Job types are matched by name, and their first rate is dated before the lock of the demo.
    [SqlServerFact]
    public async Task M3_a_rerun_after_a_demo_job_type_was_renamed_does_not_stop_the_seed()
    {
        await using var host = new Host();
        Assert.Equal(DeploymentBootstrapper.Success, (await host.RunAsync()).ExitCode);
        var (owner, _) = await LoginNewAsync(host.App, StaffRole.Accountant);
        var jobTypes = await owner.GetFromJsonAsync<JsonElement>(JobTypes);
        var harvesting = jobTypes.GetProperty("items").EnumerateArray().Single(j => j.Str("name") == "Thu hoạch");
        await (await owner.PutAsJsonAsync($"{JobTypes}/{harvesting.IdOf()}/name", new { name = "Thu hoạch hàu", rowVersion = harvesting.VersionOf() })).BodyAsync(HttpStatusCode.OK);

        var rerun = await Record.ExceptionAsync(host.RunAsync);

        Assert.True(rerun is null, $"The rerun threw: {rerun?.Message}");
        Assert.DoesNotContain("Thu hoạch", await host.QueryAsync(db => db.JobTypes.Select(j => j.Name).ToListAsync()));
    }
}
