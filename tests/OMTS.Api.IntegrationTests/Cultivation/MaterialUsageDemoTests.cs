using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The demo usage, on SQL Server through the real bootstrap: both kinds of group, recorded by the managers, and a rerun adds nothing.</summary>
public class MaterialUsageDemoTests
{
    [SqlServerFact]
    public async Task The_demo_has_usage_inside_open_cycles_and_with_no_cycle_by_the_zone_managers_and_a_rerun_adds_nothing()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "HTX QA",
                [BootstrapSettings.OwnerDisplayNameKey] = "Owner QA",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
                ["Storage:Local:RootPath"] = storageRoot,
            });

            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(api)).ExitCode);

            var usages = await UseDbAsync(api, db => db.MaterialUsages.AsNoTracking()
                .Select(u => new
                {
                    u.RaftId,
                    Raft = u.Raft.Code,
                    u.UsageDate,
                    u.RecordedByAccountId,
                    u.Item.Group,
                    InCycle = db.CultivationCycles.Any(c => c.RaftId == u.RaftId && c.StartDate <= u.UsageDate && (c.CloseDate == null || c.CloseDate >= u.UsageDate)),
                })
                .ToListAsync());

            // Three records on each of the four rafts with a cycle, one on each of the two without.
            Assert.Equal(14, usages.Count);
            Assert.Equal(["B1-01", "B1-02", "B1-06", "B2-01", "B2-02", "B2-06"], usages.Select(u => u.Raft).Distinct().Order());
            Assert.All(usages, u => Assert.Equal(MaterialUsage.ItemGroup, u.Group));
            Assert.All(usages, u => Assert.Equal(!u.Raft.EndsWith("06", StringComparison.Ordinal), u.InCycle));
            var managers = await UseDbAsync(api, db => db.Accounts.Where(a => a.Username == "quanlyvung1" || a.Username == "quanlyvung2").Select(a => a.Id).ToListAsync());
            Assert.All(usages, u => Assert.Contains(u.RecordedByAccountId, managers));

            // The rafts stay as the other demo steps left them, so the logs of the managers and the receipts still act.
            var rerun = await RunAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
            Assert.Equal(usages.Count, await UseDbAsync(api, db => db.MaterialUsages.CountAsync()));
            Assert.Contains(rerun.Messages, m => m == "Demo material usage: already there.");
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }

    private static async Task<T> UseDbAsync<T>(WebApplicationFactory<Program> app, Func<OmtsDbContext, Task<T>> use)
    {
        using var scope = app.Services.CreateScope();
        return await use(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }
}
