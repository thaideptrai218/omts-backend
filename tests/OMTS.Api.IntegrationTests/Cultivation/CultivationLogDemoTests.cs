using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The demo logs on demo cycles, on SQL Server through the real bootstrap: recorded by the managers, so the Owner has something to review.</summary>
public class CultivationLogDemoTests
{
    [SqlServerFact]
    public async Task The_demo_has_logs_by_the_zone_managers_on_three_rafts_of_each_zone_and_a_rerun_adds_nothing_and_brings_none_back()
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

            var logs = await UseDbAsync(api, db => db.CultivationLogs.AsNoTracking()
                .Select(l => new { l.Id, Raft = l.Cycle.Raft.Code, l.VisitDate, l.Cycle.StartDate, l.RecordedByAccountId, Photos = l.Photos.Select(p => p.FileKey).ToList() })
                .ToListAsync());
            Assert.Equal(["B1-01", "B1-02", "B1-03", "B2-01", "B2-02", "B2-03"], logs.Select(l => l.Raft).Distinct().Order());
            Assert.All(logs, l => Assert.True(l.VisitDate > l.StartDate));
            Assert.All(logs, l => Assert.NotEmpty(l.Photos));
            var managers = await UseDbAsync(api, db => db.Accounts.Where(a => a.Username == "quanlyvung1" || a.Username == "quanlyvung2").Select(a => a.Id).ToListAsync());
            Assert.All(logs, l => Assert.Contains(l.RecordedByAccountId, managers));

            // The Owner has one MSG-CYCLE-04 per log, and every photo is stored under its one key.
            var ownerId = await UseDbAsync(api, db => db.Accounts.Where(a => a.Username == "owner").Select(a => a.Id).SingleAsync());
            Assert.Equal(logs.Count, await UseDbAsync(api, db => db.Notifications.CountAsync(n => n.Type == "CULTIVATION_LOG_RECORDED" && n.RecipientAccountId == ownerId)));
            var stored = Directory.GetFiles(Path.Combine(storageRoot, FileCategories.CultivationLogPhotos), "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(storageRoot, f).Replace('\\', '/'));
            Assert.Equal(logs.SelectMany(l => l.Photos).Order(), stored.Order());

            // Rafts 4 and 5 keep a cycle that can be cancelled. The steps after the logs still acted as the Owner.
            var withoutLogs = await UseDbAsync(api, db => db.CultivationCycles
                .Where(c => c.StatusId == StatusIds.CultivationCycle.Open && !db.CultivationLogs.Any(l => l.CycleId == c.Id)).Select(c => c.Raft.Code).ToListAsync());
            Assert.Contains("B1-04", withoutLogs);
            Assert.Contains("B1-05", withoutLogs);
            var receiptActors = await UseDbAsync(api, db => db.PurchaseReceipts.Select(r => r.RecordedByAccountId).Distinct().ToListAsync());
            Assert.Equal(ownerId, Assert.Single(receiptActors));

            // A removed log stays removed on a rerun, and nothing else is added.
            var gone = logs[0].Id;
            await UseDbAsync(api, async db =>
            {
                await db.CultivationLogPhotos.Where(p => p.LogId == gone).ExecuteDeleteAsync();
                return await db.CultivationLogs.Where(l => l.Id == gone).ExecuteDeleteAsync();
            });

            var rerun = await RunAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
            Assert.Equal(logs.Count - 1, await UseDbAsync(api, db => db.CultivationLogs.CountAsync()));
            Assert.Contains(rerun.Messages, m => m == "Demo cultivation logs: already there.");
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
