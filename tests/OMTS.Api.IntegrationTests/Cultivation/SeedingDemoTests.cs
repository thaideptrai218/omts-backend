using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The demo seeding, on SQL Server through the real bootstrap: sessions by the managers within the rules, one mark per zone, the receipts blocked, and a rerun adds nothing.</summary>
public class SeedingDemoTests
{
    [SqlServerFact]
    public async Task The_demo_has_sessions_by_the_zone_managers_within_the_rules_and_a_rerun_adds_nothing()
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

            var sessions = await UseDbAsync(api, db => db.SeedingRecords.AsNoTracking()
                .Select(r => new
                {
                    Raft = r.Cycle.Raft.Code,
                    r.SeedingDate,
                    r.Cycle.StartDate,
                    Received = r.SeedLotLine.Receipt.ReceiptDate,
                    Status = r.SeedLotLine.Receipt.StatusId,
                    r.RecordedByAccountId,
                    Lanes = r.Lanes.Count,
                })
                .ToListAsync());

            Assert.Equal(["B1-01", "B1-02", "B1-03", "B2-01", "B2-02", "B2-03"], sessions.Select(s => s.Raft).Distinct().Order());
            Assert.Equal(8, sessions.Count);
            Assert.All(sessions, s =>
            {
                Assert.True(s.SeedingDate >= s.StartDate && s.SeedingDate >= s.Received, $"{s.Raft} on {s.SeedingDate}");
                Assert.Equal(StatusIds.PurchaseReceipt.Completed, s.Status);
                Assert.True(s.Lanes >= 1);
            });
            var managers = await UseDbAsync(api, db => db.Accounts.Where(a => a.Username == "quanlyvung1" || a.Username == "quanlyvung2").Select(a => a.Id).ToListAsync());
            Assert.All(sessions, s => Assert.Contains(s.RecordedByAccountId, managers));

            // The first raft of each zone is marked complete, so the Owner has one MSG-CYCLE-02 for it; the others are not.
            var marked = await UseDbAsync(api, db => db.CultivationCycles.AsNoTracking().Where(c => c.SeedingCompletedOn != null).Select(c => c.Raft.Code).OrderBy(c => c).ToListAsync());
            Assert.Equal(["B1-01", "B2-01"], marked);
            var owner = await UseDbAsync(api, db => db.Accounts.Where(a => a.Username == "owner").Select(a => a.Id).SingleAsync());
            Assert.Equal(2, await UseDbAsync(api, db => db.Notifications.CountAsync(n => n.Type == "CYCLE_SEEDING_COMPLETED" && n.RecipientAccountId == owner)));

            // The fourth and fifth rafts keep a cycle with no record, which can still be cancelled.
            Assert.Equal(0, await UseDbAsync(api, db => db.SeedingRecords.CountAsync(r => r.Cycle.Raft.Code == "B1-04" || r.Cycle.Raft.Code == "B2-05")));

            var rerun = await RunAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
            Assert.Equal(sessions.Count, await UseDbAsync(api, db => db.SeedingRecords.CountAsync()));
            Assert.Equal(2, await UseDbAsync(api, db => db.Notifications.CountAsync(n => n.Type == "CYCLE_SEEDING_COMPLETED" && n.RecipientAccountId == owner)));
            Assert.Contains(rerun.Messages, m => m == "Demo seeding sessions: already there.");
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static async Task<T> UseDbAsync<T>(WebApplicationFactory<Program> api, Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }
}
