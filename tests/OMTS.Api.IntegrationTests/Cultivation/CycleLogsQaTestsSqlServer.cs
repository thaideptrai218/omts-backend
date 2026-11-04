using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Storage;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of PR C4 on SQL Server: real row versions, column sizes, constraints and the real demo bootstrap.</summary>
public class CycleLogsQaTestsSqlServer
{
    private const string Category = FileCategories.CultivationLogPhotos;

    private static Task<List<string>> KeysInDatabaseAsync(FarmingSqlServerHost host) =>
        host.QueryAsync(db => db.CultivationLogPhotos.Select(p => p.FileKey).OrderBy(k => k).ToListAsync());

    // FINDING M2 (BR-CYCLE-05 "does not limit the number of logs", NFR-09 "no version check"): a new log carries no version and edits nothing another user saved, yet it is
    // refused because any other log of the cycle was saved during its upload. The cycle is read before the photos are stored, so the window is the whole upload. Fails on purpose: 409 MSG06.
    [SqlServerFact]
    public async Task M2_a_new_log_is_not_refused_because_another_log_of_the_cycle_was_saved_during_its_upload()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var hold = new UploadHold();
        await using var api = host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IFileStorage>(provider => new HeldStorage(provider.GetRequiredService<LocalFileStorage>(), hold))));
        var client = api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, host.Owner.Account.Username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var cycle = await client.StartCycleAsync((await client.CreateRaftAsync((await client.CreateZoneAsync()).Id())).Id());
        hold.Arm();

        var slow = client.PostLogAsync(cycle.Id(), new LogFields { Notes = "slow connection" });
        await hold.Reached;
        await client.RecordLogAsync(cycle.Id(), new LogFields { Notes = "saved meanwhile" });
        hold.Release();
        var answer = await slow;

        // Whole or nothing either way.
        Assert.Equal(await KeysInDatabaseAsync(host), host.StoredKeys(Category));
        Assert.True(answer.StatusCode == HttpStatusCode.Created, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
    }

    // FINDING M2, the same bump seen from the plan: nobody edited the plan, yet the form is stale. NFR-09 speaks of two users editing the same record. Fails on purpose: 409 MSG06.
    [SqlServerFact]
    public async Task M2_a_plan_form_stays_valid_when_a_log_was_recorded_meanwhile()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var cycle = await owner.StartCycleAsync((await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id());
        await owner.RecordLogAsync(cycle.Id());

        var answer = await owner.PutCycleAsync(cycle.Id(), new CycleBody(cycle.Date("startDate"), TargetYieldKg: 1000m, RowVersion: cycle.Text("rowVersion")));

        Assert.True(answer.StatusCode == HttpStatusCode.OK, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
    }

    // The longest values the checks allow must fit the columns, with ten keys in the audit row.
    [SqlServerFact]
    public async Task The_longest_notes_and_reason_and_ten_photos_fit_the_columns_and_the_audit_log()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var cycle = await owner.StartCycleAsync((await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id());
        var notes = new string('ệ', 2000);
        var reason = new string('ạ', 1000);

        var log = await owner.RecordLogAsync(cycle.Id(), new LogFields { Notes = notes, Photos = Enumerable.Repeat(Jpeg, 10).ToList() });
        Assert.Equal(notes, log.Text("notes"));

        var edit = new LogFields { Notes = new string('ố', 2000), Reason = reason, RowVersion = log.Text("rowVersion"), Photos = Enumerable.Repeat(Png, 10).ToList() };
        var saved = (await (await owner.PutLogAsync(log.Id(), edit)).ReadAsync(HttpStatusCode.OK)).GetProperty("log");

        Assert.Equal(Enumerable.Range(1, 10), saved.PhotoSlots());
        Assert.Equal(saved.PhotoKeys().Order(), host.StoredKeys(Category));
        var audit = await host.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "CultivationLog.Corrected" && e.RecordId == log.Id()));
        Assert.Equal(reason, audit.Reason);
        Assert.All(log.PhotoKeys(), k => Assert.Contains(k, audit.Before));
        Assert.All(saved.PhotoKeys(), k => Assert.Contains(k, audit.After));

        await (await owner.RemoveLogAsync(log.Id(), reason, saved.Text("rowVersion"))).ReadAsync(HttpStatusCode.NoContent);
        Assert.Empty(host.StoredKeys(Category));
    }

    [SqlServerFact]
    public async Task The_check_constraint_holds_both_ends_and_the_unique_index_holds_per_log()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var cycle = await owner.StartCycleAsync((await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id());
        var log = await owner.RecordLogAsync(cycle.Id());
        var other = await owner.RecordLogAsync(cycle.Id());
        CultivationLogPhoto Photo(Guid logId, int slot) => new() { LogId = logId, SortOrder = slot, FileKey = "x" };

        foreach (var slot in new[] { 0, -1, 11 })
        {
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.CultivationLogPhotos.Add(Photo(log.Id(), slot))));
            Assert.Contains("CK_CultivationLogPhoto_SortOrder", refused.InnerException!.Message);
        }

        await host.UseDbAsync(db => db.CultivationLogPhotos.Add(Photo(log.Id(), 10)));
        await host.UseDbAsync(db => db.CultivationLogPhotos.Add(Photo(other.Id(), 10)));
        var taken = await Assert.ThrowsAsync<DbUpdateException>(() => host.UseDbAsync(db => db.CultivationLogPhotos.Add(Photo(log.Id(), 10))));
        Assert.Contains("IX_CultivationLogPhotos_LogId_SortOrder", taken.InnerException!.Message);
    }

    // A rerun adds no row, no file, no notification and no audit row, also after a log was really removed with its files.
    [SqlServerFact]
    public async Task A_demo_rerun_changes_nothing_after_a_real_removal()
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
            Assert.Equal(DeploymentBootstrapper.Success, (await BootstrapAsync(api)).ExitCode);

            var removed = await UseAsync(api, db => db.CultivationLogs.AsNoTracking().OrderBy(l => l.Id).Select(l => new { l.Id, l.RowVersion, l.RecordedByAccountId }).FirstAsync());
            using (var scope = api.Services.CreateScope())
            using (scope.ServiceProvider.GetRequiredService<AmbientActor>().Use(removed.RecordedByAccountId))
            {
                var answer = await scope.ServiceProvider.GetRequiredService<CultivationLogCorrectionService>()
                    .RemoveAsync(removed.Id, new RemoveCultivationLogRequest("wrong raft", Convert.ToBase64String(removed.RowVersion)), CancellationToken.None);
                Assert.True(answer.IsSuccess);
            }

            var before = await CountsAsync(api, storageRoot);

            var rerun = await BootstrapAsync(api);
            var again = await BootstrapAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
            Assert.Equal(DeploymentBootstrapper.Success, again.ExitCode);
            Assert.Contains(rerun.Messages, m => m == "Demo cultivation logs: already there.");
            Assert.Equal(before, await CountsAsync(api, storageRoot));
            Assert.False(await UseAsync(api, db => db.CultivationLogs.AnyAsync(l => l.Id == removed.Id)));
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static async Task<(int Logs, int Photos, int Notifications, int Audit, int Files)> CountsAsync(WebApplicationFactory<Program> api, string storageRoot) => (
        await UseAsync(api, db => db.CultivationLogs.CountAsync()),
        await UseAsync(api, db => db.CultivationLogPhotos.CountAsync()),
        await UseAsync(api, db => db.Notifications.CountAsync(n => n.Type == "CULTIVATION_LOG_RECORDED")),
        await UseAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordType == nameof(CultivationLog))),
        Directory.GetFiles(Path.Combine(storageRoot, Category), "*", SearchOption.AllDirectories).Length);

    private static async Task<BootstrapOutcome> BootstrapAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }

    private static async Task<T> UseAsync<T>(WebApplicationFactory<Program> api, Func<OmtsDbContext, Task<T>> use)
    {
        using var scope = api.Services.CreateScope();
        return await use(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }
}
