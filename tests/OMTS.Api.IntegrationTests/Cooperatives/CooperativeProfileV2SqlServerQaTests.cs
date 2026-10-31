using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeCertificationRequests;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileQaKit;
using static OMTS.Api.IntegrationTests.Cooperatives.CooperativeProfileRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// QA of OMTS-217 PR 0.7 on SQL Server: the row version after uploads, concurrent edits and uploads (no stray file),
/// the CooperativeProfileV2 migration down and up again with its column lengths, readiness and bootstrap on a database
/// migrated from before it, and the demo seed completing a profile seeded before it.
/// </summary>
public class CooperativeProfileV2SqlServerQaTests
{
    private const string V2 = "_CooperativeProfileV2";

    // An upload or removal of the logo or the cover photo writes the cooperative row: its answer carries the version the
    // next profile save needs, and the version read before it is stale. A certification lives in its own row.
    [SqlServerFact]
    public async Task Upload_and_remove_answers_carry_the_row_version_the_next_profile_save_needs()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var saved = await SaveAsync(owner, await RowVersionAsync(owner), "a@htxtrungnam.vn");

        var afterCover = (await (await owner.UploadCoverPhotoAsync(FarmingTestKit.Png)).ReadAsync(HttpStatusCode.OK)).GetProperty("profile").Text("rowVersion");
        var stale = await owner.PutAsJsonAsync(Profile, Valid(saved, b => b["email"] = "b@htxtrungnam.vn"));
        Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (stale.StatusCode, await stale.ErrorCodeAsync()));
        Assert.NotEqual(saved, afterCover);

        var next = await SaveAsync(owner, afterCover, "c@htxtrungnam.vn");
        var afterLogo = (await (await UploadLogoAsync(owner, FarmingTestKit.Png, "logo.png")).ReadAsync(HttpStatusCode.OK)).Text("rowVersion");
        next = await SaveAsync(owner, afterLogo, "d@htxtrungnam.vn");
        var afterRemove = (await (await owner.DeleteAsync(CoverPhoto)).ReadAsync(HttpStatusCode.OK)).GetProperty("profile").Text("rowVersion");
        next = await SaveAsync(owner, afterRemove, "e@htxtrungnam.vn");
        await owner.CreateCertificationAsync();
        next = await SaveAsync(owner, next, "f@htxtrungnam.vn");

        Assert.Equal(next, await RowVersionAsync(owner));
        Assert.Equal("f@htxtrungnam.vn", await host.QueryAsync(db => db.Cooperatives.Select(c => c.Email).SingleAsync()));
    }

    // Four edits of one version, each with a new file: one wins (MSG-GEN-05), the others get MSG06; only the winner's
    // file stays stored (the losers' uploads and the replaced file are removed), and one change is audited.
    [SqlServerFact]
    public async Task Parallel_edits_of_one_certification_version_let_one_win_and_leave_only_its_file()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var created = await owner.CreateCertificationAsync(new CertificationFields { Name = "VietGAP", Number = "VG-0" });
        var (id, version) = (created.Id(), created.Text("rowVersion"));

        var responses = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => owner.PutAsync($"{Certifications}/{id}", new CertificationFields
        {
            Name = "VietGAP", Number = $"VG-{i}", RowVersion = version, File = FarmingTestKit.Png, FileName = $"v{i}.png",
        }.ToForm())));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var lost in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (lost.StatusCode, await lost.ErrorCodeAsync()));
        }

        var stored = await host.QueryAsync(db => db.CooperativeCertifications.AsNoTracking().SingleAsync());
        Assert.NotEqual("VG-0", stored.Number);
        Assert.Equal([stored.FileUrl!], host.StoredKeys(FileCategories.CooperativeCertifications));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "CooperativeCertification.Updated")));
    }

    // Uploads racing each other: each is saved or answers MSG06, and the storage ends with exactly the current cover.
    [SqlServerFact]
    public async Task Parallel_cover_uploads_end_with_one_cover_and_only_its_file_stored()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        await (await owner.UploadCoverPhotoAsync(FarmingTestKit.Png)).ReadAsync(HttpStatusCode.OK);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            i % 2 == 0 ? owner.UploadCoverPhotoAsync(FarmingTestKit.Png) : owner.UploadCoverPhotoAsync(FarmingTestKit.Jpeg, "cover.jpg")));

        foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (response.StatusCode, await response.ErrorCodeAsync()));
        }

        var current = await host.QueryAsync(db => db.Cooperatives.Select(c => c.CoverPhotoUrl).SingleAsync());
        Assert.Equal([current!], host.StoredKeys(FileCategories.CooperativeCoverPhotos));
        var url = (await owner.GetFromJsonAsync<JsonElement>(Profile)).GetProperty("coverPhotoUrl").GetString();
        Assert.Equal(HttpStatusCode.OK, (await host.Api.CreateClient().GetAsync(url)).StatusCode);
    }

    // Down puts the statuses back as they were (201 initial, 202), Completed for a cooperative with a tax code; Up again
    // removes them, keeps the rows and matches the model. The new columns have the lengths FARM-01 needs.
    [SqlServerFact]
    public async Task Migration_down_and_up_again_round_trips_with_the_v2_column_lengths()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        var v2 = all.Single(m => m.EndsWith(V2, StringComparison.Ordinal));
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(v2);
        var (completed, preliminary) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Cooperatives (Id, FullName, ShortName, TaxCode, CoverPhotoUrl, CreatedAt) VALUES ({completed}, N'HTX A', N'A', N'0101234567', N'cooperative-covers/x.png', SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ({preliminary}, N'HTX B', SYSDATETIMEOFFSET())");

        Assert.Equal(
            [("CoverPhotoUrl", 2000), ("IntroductionEn", 8000), ("IntroductionVi", 8000), ("ShortName", 200), ("SocialLink", 2000)],
            await ColumnLengthsAsync(db, "Cooperatives", "ShortName", "SocialLink", "CoverPhotoUrl", "IntroductionVi", "IntroductionEn"));
        Assert.Equal(
            [("FileUrl", 2000), ("Issuer", 400), ("Name", 400), ("Number", 200)],
            await ColumnLengthsAsync(db, "CooperativeCertifications", "Name", "Number", "Issuer", "FileUrl"));
        Assert.Equal(1, await ScalarAsync(db,
            "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = 'FK_CooperativeCertifications_Cooperatives_CooperativeId' AND delete_referential_action = 0"));

        await migrator.MigrateAsync(all[all.IndexOf(v2) - 1]);

        Assert.Equal(202, await db.Database.SqlQuery<int>($"SELECT StatusId AS [Value] FROM Cooperatives WHERE Id = {completed}").SingleAsync());
        Assert.Equal(201, await db.Database.SqlQuery<int>($"SELECT StatusId AS [Value] FROM Cooperatives WHERE Id = {preliminary}").SingleAsync());
        Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE [Group] = 'COOPERATIVE'"));
        Assert.Equal(201, await ScalarAsync(db, "SELECT Id AS [Value] FROM Statuses WHERE [Group] = 'COOPERATIVE' AND IsInitial = 1"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = 'FK_Cooperatives_Statuses_StatusId'"));

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE [Group] = 'COOPERATIVE' OR Id IN (201, 202)"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM StatusTransitions WHERE Id = 3"));
        Assert.Equal(["HTX A", "HTX B"], await db.Cooperatives.OrderBy(c => c.FullName).Select(c => c.FullName).ToListAsync());
    }

    // A deployment migrated from before v2 (a Completed cooperative, statuses 201–202 in use): the bootstrap applies
    // the migration and finds the cooperative, readiness is green, and the Owner reads and saves the profile.
    [SqlServerFact]
    public async Task Bootstrap_and_readiness_work_on_a_database_migrated_from_before_v2()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using (var db = database.CreateContext())
        {
            var all = db.Database.GetMigrations().ToList();
            await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith(V2, StringComparison.Ordinal)) - 1]);
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO Cooperatives (Id, FullName, TaxCode, StatusId, CreatedAt) VALUES ({Guid.NewGuid()}, N'HTX Trung Nam', N'0101234567', 202, SYSDATETIMEOFFSET())");
        }

        await using var api = new SqlServerApiFactory(database);
        BootstrapOutcome outcome;
        using (var scope = api.Services.CreateScope())
        {
            outcome = await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(false, CancellationToken.None);
        }

        var password = NewPassword();
        var account = await api.AddAccountAsync(NewCode(), password);
        var owner = api.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginTokensAsync(owner, account.Username, password)).GetProperty("accessToken").GetString());
        var profile = await owner.GetFromJsonAsync<JsonElement>(Profile);

        Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
        Assert.Contains("Database migrations applied.", outcome.Messages);
        Assert.Contains(outcome.Messages, m => m.Contains("already initialized"));
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/health/ready")).StatusCode);
        Assert.Equal(("0101234567", JsonValueKind.Null), (profile.GetProperty("taxCode").GetString(), profile.GetProperty("shortName").ValueKind));
        Assert.DoesNotContain("status", profile.EnumerateObject().Select(p => p.Name));
        var saved = await owner.PutAsJsonAsync(Profile, Valid(profile.GetProperty("rowVersion").GetString(), b => b["taxCode"] = "0101234567"));
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
    }

    // A demo database seeded before v2 (profile complete, no short name, cover or certification) gets the new fields on
    // the next demo run, keeps what the Owner changed, and a further run creates nothing.
    [SqlServerFact]
    public async Task Demo_run_completes_a_profile_seeded_before_v2_keeps_the_owners_changes_and_reruns_create_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-qa-demo-{Guid.NewGuid():N}");
        await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
        {
            [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã Nuôi hàu Vân Đồn",
            [BootstrapSettings.OwnerDisplayNameKey] = "Chủ nhiệm",
            [BootstrapSettings.OwnerUsernameKey] = "owner",
            [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
            [BootstrapSettings.OwnerTemporaryPasswordKey] = NewPassword(),
            ["Storage:Local:RootPath"] = storageRoot,
        });
        try
        {
            Assert.Equal(DeploymentBootstrapper.Success, (await DemoAsync(api)).ExitCode);
            string? taxCode = null;
            await ChangeAsync(api, db =>
            {
                var c = db.Cooperatives.Single();
                taxCode = c.TaxCode;
                (c.ShortName, c.YearEstablished, c.SocialLink, c.IntroductionVi, c.IntroductionEn, c.CoverPhotoUrl) = (null, null, null, null, null, null);
                c.Email = "banhang@htx-vandon.example.com";
                db.CooperativeCertifications.RemoveRange(db.CooperativeCertifications);
            });

            var upgrade = await DemoAsync(api);
            var completed = await FarmLabSqlServerKit.QueryAsync(api, db => db.Cooperatives.AsNoTracking().SingleAsync());
            var again = await DemoAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, upgrade.ExitCode);
            // The profile save, the cover photo and the certification; the logo was there already.
            Assert.Contains("Demo cooperative profile: 3 created.", upgrade.Messages);
            Assert.Equal(("HTX Hàu Vân Đồn", 2012, taxCode, "banhang@htx-vandon.example.com"), (completed.ShortName, completed.YearEstablished, completed.TaxCode, completed.Email));
            Assert.All(new[] { completed.SocialLink, completed.IntroductionVi, completed.IntroductionEn, completed.CoverPhotoUrl }, v => Assert.False(string.IsNullOrEmpty(v)));
            Assert.Contains("Demo cooperative profile: already there.", again.Messages);
            Assert.Equal(1, await FarmLabSqlServerKit.QueryAsync(api, db => db.CooperativeCertifications.CountAsync()));
            Assert.Equal(completed.CoverPhotoUrl, await FarmLabSqlServerKit.QueryAsync(api, db => db.Cooperatives.Select(c => c.CoverPhotoUrl).SingleAsync()));
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static async Task<string> SaveAsync(HttpClient owner, string? rowVersion, string email) =>
        (await (await owner.PutAsJsonAsync(Profile, Valid(rowVersion, b => b["email"] = email))).ReadAsync(HttpStatusCode.OK))
            .GetProperty("profile").Text("rowVersion");

    private static async Task<BootstrapOutcome> DemoAsync(SqlServerApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }

    private static Task ChangeAsync(SqlServerApiFactory api, Action<OmtsDbContext> change) => FarmLabSqlServerKit.ChangeAsync(api, change);

    /// <summary>(column, max_length in bytes) of the named columns, by name.</summary>
    private static async Task<List<(string, int)>> ColumnLengthsAsync(OmtsDbContext db, string table, params string[] columns)
    {
        var lengths = new List<(string, int)>();
        foreach (var column in columns.Order(StringComparer.Ordinal))
        {
            lengths.Add((column, await db.Database
                .SqlQuery<int>($"SELECT CAST(max_length AS int) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({table}) AND name = {column}")
                .SingleAsync()));
        }

        return lengths;
    }

    /// <summary>A fixed query (test constants only) returning one number as <c>[Value]</c>.</summary>
    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
