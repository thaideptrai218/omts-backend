using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of the migration and of the demo step on SQL Server through the real bootstrap: reruns, changed data on an older database, and the shared actor helper.</summary>
public class MaterialUsagesQaTestsMigrationAndDemo
{
    private const string MigrationBefore = "20261104124405_CultivationLogs";

    /// <summary>The constraint comes back with the table: Up, Down, Up, then the table refuses what it refused the first time.</summary>
    [SqlServerFact]
    public async Task After_up_down_up_the_check_constraint_and_the_foreign_keys_work_again()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        await migrator.MigrateAsync(MigrationBefore);
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());

        var raft = new Raft { Zone = new FarmingZone { Cooperative = new Cooperative { FullName = "HTX" }, Code = "KV01", Name = "A" }, Code = "B01", Size = "1" };
        var item = new FarmingInputItem { Code = "MAT", Name = "Buoy", Group = MaterialUsage.ItemGroup, Unit = "piece" };
        var account = new Account { Username = "owner", DisplayName = "Owner", SubjectType = SubjectType.CooperativeOwner, StatusId = StatusIds.Account.Active };
        db.AddRange(raft, item, account);
        await db.SaveChangesAsync();

        MaterialUsage Usage(decimal quantity, Guid? itemId = null) => new()
        {
            RaftId = raft.Id, ItemId = itemId ?? item.Id, UsageDate = new DateOnly(2026, 2, 1), RepairReason = "x", Quantity = quantity, RecordedByAccountId = account.Id,
        };

        foreach (var (bad, name) in new[] { (Usage(0m), "CK_MaterialUsage_Quantity"), (Usage(-1m), "CK_MaterialUsage_Quantity"), (Usage(1m, Guid.NewGuid()), "FK_MaterialUsages_FarmingInputItems_ItemId") })
        {
            db.MaterialUsages.Add(bad);
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(name, refused.InnerException!.Message);
            db.ChangeTracker.Clear();
        }

        db.MaterialUsages.Add(Usage(0.001m));
        await db.SaveChangesAsync();
        Assert.Equal(0.001m, (await db.MaterialUsages.AsNoTracking().SingleAsync()).Quantity);
    }

    /// <summary>The whole seed, a change by real use, then a rerun: nothing is added and nothing brought back; the date and text of the client list hold.</summary>
    [SqlServerFact]
    public async Task A_rerun_adds_nothing_and_brings_nothing_back_after_a_real_edit_a_move_and_a_revoked_manager()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = NewApi(database, storageRoot);
            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(api)).ExitCode);
            var today = BusinessClock.ToBusinessDate(DateTimeOffset.UtcNow);

            // The client list: two repairs ten and twenty days after the start, three records; one seven days ago with no cycle.
            var rows = await QueryAsync(api, db => db.MaterialUsages.AsNoTracking()
                .Select(u => new { u.Id, u.RaftId, Raft = u.Raft.Code, u.UsageDate, u.Quantity, Start = db.CultivationCycles.Where(c => c.RaftId == u.RaftId).Select(c => (DateOnly?)c.StartDate).FirstOrDefault() })
                .ToListAsync());
            foreach (var raft in rows.GroupBy(r => r.Raft))
            {
                if (raft.Key.EndsWith("06", StringComparison.Ordinal))
                {
                    Assert.Equal(today.AddDays(-7), raft.Single().UsageDate);
                }
                else
                {
                    var start = raft.First().Start!.Value;
                    Assert.Equal(2, raft.Count(r => r.UsageDate == start.AddDays(10)));
                    Assert.Equal(1, raft.Count(r => r.UsageDate == start.AddDays(20)));
                }
            }

            var audits = await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordType == "MaterialUsage"));
            Assert.Equal(rows.Count, audits);
            Assert.Equal(0, await QueryAsync(api, db => db.Notifications.CountAsync(n => n.Title.Contains("usage"))));

            // Recorded and written to the log by the managers, with the first two items of the group by code.
            var managers = await QueryAsync(api, db => db.Accounts.Where(a => a.Username == "quanlyvung1" || a.Username == "quanlyvung2").Select(a => (Guid?)a.Id).ToListAsync());
            var actors = await QueryAsync(api, db => db.AuditEvents.Where(e => e.RecordType == "MaterialUsage").Select(e => e.ActorAccountId).ToListAsync());
            Assert.All(actors, a => Assert.Contains(a, managers));
            var firstItems = await QueryAsync(api, db => db.FarmingInputItems.Where(i => i.Group == MaterialUsage.ItemGroup && i.StatusId == StatusIds.Usage.InUse).OrderBy(i => i.Code).Select(i => i.Id).Take(2).ToListAsync());
            var usedItems = await QueryAsync(api, db => db.MaterialUsages.Select(u => u.ItemId).Distinct().ToListAsync());
            Assert.Equal(firstItems.Order(), usedItems.Order());

            // Changed by use: one quantity, one record on another raft, and the manager of the second zone revoked.
            var (edited, movedAway) = (rows.First(r => r.Raft == "B1-01"), rows.Single(r => r.Raft == "B2-06"));
            var targetRaft = rows.First(r => r.Raft == "B1-02").RaftId;
            await UseDbAsync(api, async db =>
            {
                (await db.MaterialUsages.SingleAsync(u => u.Id == edited.Id)).Quantity = 99m;
                (await db.MaterialUsages.SingleAsync(u => u.Id == movedAway.Id)).RaftId = targetRaft;
                await RevokeAsync(db, "quanlyvung2");
            });

            var rerun = await RunAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
            Assert.Contains(rerun.Messages, m => m == "Demo material usage: already there.");
            Assert.Equal(rows.Count, await QueryAsync(api, db => db.MaterialUsages.CountAsync()));
            Assert.Equal(audits, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordType == "MaterialUsage")));
            Assert.Equal(99m, await QueryAsync(api, db => db.MaterialUsages.Where(u => u.Id == edited.Id).Select(u => u.Quantity).SingleAsync()));
            Assert.Equal(targetRaft, await QueryAsync(api, db => db.MaterialUsages.Where(u => u.Id == movedAway.Id).Select(u => u.RaftId).SingleAsync()));
        }
        finally
        {
            DeleteFolder(storageRoot);
        }
    }

    /// <summary>
    /// The promise of the step: a raft that changed on an older database is passed over, the seed goes on, and a manager who lost the zone leaves the work to the Owner.
    /// The step sees an older database as one with no usage yet.
    /// </summary>
    [SqlServerFact]
    public async Task On_an_older_database_a_changed_raft_is_passed_over_and_the_owner_stands_in_for_a_manager_who_lost_the_zone()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = NewApi(database, storageRoot);
            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(api)).ExitCode);
            var today = BusinessClock.ToBusinessDate(DateTimeOffset.UtcNow);

            await UseDbAsync(api, async db =>
            {
                await db.AuditEvents.Where(e => e.RecordType == "MaterialUsage").ExecuteDeleteAsync();
                await db.MaterialUsages.ExecuteDeleteAsync();

                // B1-01 too young, B1-02 Discontinued, B1-06 a closed cycle on the date of its repair, B2-06 Discontinued.
                var young = await db.CultivationCycles.SingleAsync(c => c.Raft.Code == "B1-01");
                young.StartDate = today.AddDays(-19);
                (await db.Rafts.SingleAsync(r => r.Code == "B1-02")).StatusId = StatusIds.Usage.Discontinued;
                var noCycle = await db.Rafts.SingleAsync(r => r.Code == "B1-06");
                db.CultivationCycles.Add(new CultivationCycle { RaftId = noCycle.Id, StartDate = today.AddDays(-10), CloseDate = today.AddDays(-5), StatusId = StatusIds.CultivationCycle.Closed });
                (await db.Rafts.SingleAsync(r => r.Code == "B2-06")).StatusId = StatusIds.Usage.Discontinued;
                await RevokeAsync(db, "quanlyvung2");
            });

            var rerun = await RunAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
            var rows = await QueryAsync(api, db => db.MaterialUsages.AsNoTracking().Select(u => new { Raft = u.Raft.Code, u.RecordedByAccountId }).ToListAsync());
            Assert.Equal(["B2-01", "B2-02"], rows.Select(r => r.Raft).Distinct().Order());
            var owner = await QueryAsync(api, db => db.Accounts.Where(a => a.SubjectType == SubjectType.CooperativeOwner).Select(a => a.Id).SingleAsync());
            Assert.All(rows, r => Assert.Equal(owner, r.RecordedByAccountId));
        }
        finally
        {
            DeleteFolder(storageRoot);
        }
    }

    /// <summary>Revokes the pairs through a tracked save, so the permission cache hears of it as it does from the real revoke.</summary>
    private static async Task RevokeAsync(OmtsDbContext db, string username)
    {
        var manager = await db.Accounts.SingleAsync(a => a.Username == username);
        foreach (var grant in await db.AccessGrants.Where(g => g.AccountId == manager.Id).ToListAsync())
        {
            (grant.StatusId, grant.RevokedAt, grant.RevokeReason) = (StatusIds.AccessGrant.Revoked, DateTimeOffset.UtcNow, "Moved");
        }
    }

    private static SqlServerApiFactory NewApi(SqlServerTestDatabase database, string storageRoot) => new(database, new Dictionary<string, string?>
    {
        [BootstrapSettings.CooperativeFullNameKey] = "HTX QA",
        [BootstrapSettings.OwnerDisplayNameKey] = "Owner QA",
        [BootstrapSettings.OwnerUsernameKey] = "owner",
        [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
        [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
        ["Storage:Local:RootPath"] = storageRoot,
    });

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }

    private static async Task<T> QueryAsync<T>(WebApplicationFactory<Program> app, Func<OmtsDbContext, Task<T>> use)
    {
        using var scope = app.Services.CreateScope();
        return await use(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    private static async Task UseDbAsync(WebApplicationFactory<Program> app, Func<OmtsDbContext, Task> change)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }
}
