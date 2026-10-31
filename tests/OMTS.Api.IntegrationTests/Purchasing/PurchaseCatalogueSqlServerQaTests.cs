using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PR P1 (OMTS-218) on a real SQL Server: the unique code and name of input items under parallel saves (GBR-03,
/// BR-PURCHASE-10), the VT proposal taken twice, query translation of the case-insensitive checks, the one Seed unit,
/// the migration PurchaseCatalogueV2 up and down, and the demo seed with the VT codes run again.
/// </summary>
public class PurchaseCatalogueSqlServerQaTests
{
    private const string Items = "/api/farming-input-items";

    [SqlServerFact]
    public async Task Parallel_creates_of_one_name_in_any_case_keep_one_item_and_point_the_others_to_it()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var name = $"Phao xốp HDPE {NewCode()}";
        var spellings = new[] { name, name.ToUpperInvariant(), name.ToLowerInvariant() };

        var responses = await InParallelAsync(6, i => owner.PostAsJsonAsync(Items, new { code = $"P{i}-{NewCode()}", name = spellings[i % 3], group = "Material", unit = "cái" }));

        var winnerId = await WinnerAsync(responses, HttpStatusCode.Created, "Name");
        // The SQL Server collation ignores case, so this counts every spelling.
        Assert.Equal(1, await host.QueryAsync(db => db.FarmingInputItems.CountAsync(i => i.Name == name)));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "FarmingInputItem.Created" && e.RecordId == winnerId)));
    }

    [SqlServerFact]
    public async Task Two_owners_saving_one_proposed_code_together_keep_one_item_and_the_next_proposal_moves_on()
    {
        await using var host = await FarmingHost.StartAsync();
        var first = host.Owner.Client;
        var second = await host.LoginAsync((await host.Api.AddAccountAsync(NewCode(), Password)).Username);
        Assert.Equal("VT001", await ProposedAsync(first));
        Assert.Equal("VT001", await ProposedAsync(second));

        // Both Add item forms hold VT001 (one typed in lower case); the Owners save at the same time.
        var responses = await InParallelAsync(4, i => (i % 2 == 0 ? first : second).PostAsJsonAsync(Items, new
        {
            code = i < 2 ? "VT001" : "vt001", name = $"Găng tay {i} {NewCode()}", group = "Other", unit = "đôi",
        }));

        var winnerId = await WinnerAsync(responses, HttpStatusCode.Created, "Code");
        Assert.Equal("VT002", await ProposedAsync(second));

        // GBR-03: the code of a discontinued item stays reserved, for the proposal and for a typed code.
        await (await first.PostAsync($"{Items}/{winnerId}/discontinue", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("VT002", await ProposedAsync(first));
        var again = await second.PostAsJsonAsync(Items, new { code = "Vt001", name = $"Găng tay {NewCode()}", group = "Other", unit = "đôi" });
        var problem = await again.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal((winnerId, "Code"), (problem.GetProperty("existingId").GetGuid(), problem.GetProperty("field").GetString()));
    }

    [SqlServerFact]
    public async Task Parallel_renames_of_three_items_to_one_name_keep_one_and_name_the_winner_to_the_others()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var ids = new List<Guid>();
        var versions = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var created = await (await owner.PostAsJsonAsync(Items, new { code = $"R{i}-{NewCode()}", name = $"Dây {i} {NewCode()}", group = "Material", unit = "cuộn" }))
                .ReadAsync(HttpStatusCode.Created);
            ids.Add(created.Id());
            versions.Add(created.GetProperty("rowVersion").GetString()!);
        }

        var name = $"Dây thừng nilon 14 mm {NewCode()}";
        var spellings = new[] { name, name.ToUpperInvariant(), name.ToLowerInvariant() };
        var codes = await host.QueryAsync(db => db.FarmingInputItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code));

        var responses = await InParallelAsync(3, i => owner.PutAsJsonAsync($"{Items}/{ids[i]}", new
        {
            code = codes[ids[i]], name = spellings[i], group = "Material", unit = "cuộn", rowVersion = versions[i],
        }));

        var winnerId = await WinnerAsync(responses, HttpStatusCode.OK, "Name");
        Assert.Contains(winnerId, ids);
        Assert.Equal([winnerId], await host.QueryAsync(db => db.FarmingInputItems.Where(i => i.Name == name).Select(i => i.Id).ToListAsync()));
    }

    [SqlServerFact]
    public async Task On_sql_server_a_discontinued_item_keeps_its_vietnamese_name_and_code_in_any_case_and_the_proposal_counts_it()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var tag = NewCode();
        var name = $"Hàu giống Thái Bình Dương {tag}";
        var first = (await (await owner.PostAsJsonAsync(Items, new { code = "vt120", name, group = "Seed", unit = "con" })).ReadAsync(HttpStatusCode.Created)).Id();
        await (await owner.PostAsync($"{Items}/{first}/discontinue", null)).ReadAsync(HttpStatusCode.OK);

        var sameName = await owner.PostAsJsonAsync(Items, new { code = "VT200", name = $"HÀU GIỐNG THÁI BÌNH DƯƠNG {tag.ToUpperInvariant()}", group = "Seed", unit = "con" });
        var nameProblem = await sameName.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal((first, "Name"), (nameProblem.GetProperty("existingId").GetGuid(), nameProblem.GetProperty("field").GetString()));
        var sameCode = await owner.PostAsJsonAsync(Items, new { code = " VT120 ", name = $"Hàu giống khác {tag}", group = "Seed", unit = "con" });
        var codeProblem = await sameCode.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal((first, "Code"), (codeProblem.GetProperty("existingId").GetGuid(), codeProblem.GetProperty("field").GetString()));

        // The proposal reads lower-case and discontinued codes, and the discontinued Seed item still sets the unit.
        var proposal = await (await owner.GetAsync($"{Items}/next-code")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("VT121", "con"), (proposal.GetProperty("code").GetString(), proposal.GetProperty("seedUnit").GetString()));
        var kg = await owner.PostAsJsonAsync(Items, new { code = "VT121", name = $"Hàu giống Cổ Trai {tag}", group = "Seed", unit = "kg" });
        Assert.Equal("con", (await kg.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01")).GetProperty("seedUnit").GetString());
    }

    // BR-PURCHASE-10: "All seed items use one unit: the Cooperative Owner chooses it with the first seed item". The check
    // reads the other Seed items before saving and nothing in the database holds the rule, so two first Seed items saved
    // together can each choose a unit (reproduced 4 times out of 4 on SQL Express). QA finding F1: accepted by the lead.
    [SqlServerFact(Skip = "Accepted: only the Owner writes items; race between the first Seed items not prevented (QA P1 F1)")]
    public async Task Parallel_first_seed_items_with_different_units_keep_one_seed_unit()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;

        var responses = await InParallelAsync(6, i => owner.PostAsJsonAsync(Items, new
        {
            code = $"S{i}-{NewCode()}", name = $"Hàu giống {i} {NewCode()}", group = "Seed", unit = i % 2 == 0 ? "con" : "kg",
        }));

        Assert.Single(await host.QueryAsync(db => db.FarmingInputItems.Where(i => i.Group == InputItemGroup.Seed).Select(i => i.Unit).Distinct().ToListAsync()));
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal("Unit", await refused.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }
    }

    [SqlServerFact]
    public async Task PurchaseCatalogueV2_converts_group_and_contacts_up_restores_them_down_and_applies_again()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var before = MigrationBefore(db);
        await migrator.MigrateAsync(before);
        var (material, seed, supplier) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO FarmingInputItems (Id, Code, Name, [Group], Unit, StatusId, CreatedAt) VALUES ({material}, N'MAT-01', N'Phao nhựa HDPE', N'RaftMaterial', N'cái', 102, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO FarmingInputItems (Id, Code, Name, [Group], Unit, StatusId, CreatedAt) VALUES ({seed}, N'SEED-01', N'Hàu giống 3 cm', N'Seed', N'con', 101, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Suppliers (Id, Name, SupplierType, PhoneNumber, OtherContacts, CertificateUrls, StatusId, CreatedAt) VALUES ({supplier}, N'Trại giống Cái Rồng', N'Seed', N'0900000031', N'Thị trấn Cái Rồng, Vân Đồn', N'[]', 101, SYSDATETIMEOFFSET())");

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var items = await db.FarmingInputItems.AsNoTracking().OrderBy(i => i.Code).ToListAsync();
        Assert.Equal(new[] { ("MAT-01", InputItemGroup.Material), ("SEED-01", InputItemGroup.Seed) }, items.Select(i => (i.Code, i.Group)));
        var converted = await db.Suppliers.AsNoTracking().SingleAsync(s => s.Id == supplier);
        Assert.Equal("Thị trấn Cái Rồng, Vân Đồn", converted.Address);
        Assert.Null(converted.Email);
        Assert.Equal(1, await UniqueIndexCountAsync(db, "IX_FarmingInputItems_Name"));
        await db.Database.ExecuteSqlAsync($"UPDATE Suppliers SET Email = N'giong@example.com' WHERE Id = {supplier}");

        await migrator.MigrateAsync(before);

        Assert.Equal("RaftMaterial", await db.Database.SqlQuery<string>($"SELECT [Group] AS [Value] FROM FarmingInputItems WHERE Id = {material}").SingleAsync());
        Assert.Equal("Seed", await db.Database.SqlQuery<string>($"SELECT [Group] AS [Value] FROM FarmingInputItems WHERE Id = {seed}").SingleAsync());
        Assert.Equal("Thị trấn Cái Rồng, Vân Đồn", await db.Database.SqlQuery<string>($"SELECT OtherContacts AS [Value] FROM Suppliers WHERE Id = {supplier}").SingleAsync());
        var columns = await db.Database.SqlQueryRaw<string>("SELECT COLUMN_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Suppliers'").ToListAsync();
        Assert.DoesNotContain("Email", columns);
        Assert.DoesNotContain("Address", columns);
        Assert.Equal(0, await UniqueIndexCountAsync(db, "IX_FarmingInputItems_Name"));

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(InputItemGroup.Material, await db.FarmingInputItems.AsNoTracking().Where(i => i.Id == material).Select(i => i.Group).SingleAsync());
    }

    [SqlServerFact]
    public async Task PurchaseCatalogueV2_stops_on_two_items_named_alike_and_leaves_the_database_as_it_was()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var before = MigrationBefore(db);
        await migrator.MigrateAsync(before);
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO FarmingInputItems (Id, Code, Name, [Group], Unit, StatusId, CreatedAt) VALUES ({first}, N'MAT-01', N'Phao nhựa', N'RaftMaterial', N'cái', 101, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO FarmingInputItems (Id, Code, Name, [Group], Unit, StatusId, CreatedAt) VALUES ({second}, N'MAT-02', N'PHAO NHỰA', N'RaftMaterial', N'cái', 102, SYSDATETIMEOFFSET())");

        await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync());

        // The migration runs in one transaction: nothing of it stays (README: rename one item first, then upgrade).
        Assert.Equal(before, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(["RaftMaterial", "RaftMaterial"], await db.Database.SqlQueryRaw<string>("SELECT [Group] AS [Value] FROM FarmingInputItems").ToListAsync());
        Assert.Contains("OtherContacts", await db.Database.SqlQueryRaw<string>("SELECT COLUMN_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Suppliers'").ToListAsync());

        await db.Database.ExecuteSqlAsync($"UPDATE FarmingInputItems SET Name = N'Phao nhựa 2' WHERE Id = {second}");
        await migrator.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [SqlServerFact]
    public async Task The_demo_catalogue_has_VT_codes_one_seed_unit_and_supplier_addresses_and_runs_again_over_older_codes()
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
                [BootstrapSettings.OwnerPhoneNumberKey] = NewPhone(),
                [BootstrapSettings.OwnerTemporaryPasswordKey] = NewPassword(),
                ["Storage:Local:RootPath"] = storageRoot,
            });

            Assert.Equal(DeploymentBootstrapper.Success, (await RunDemoAsync(api)).ExitCode);

            using (var scope = api.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
                var items = await db.FarmingInputItems.AsNoTracking().OrderBy(i => i.Code).ToListAsync();
                Assert.Equal(Enumerable.Range(1, 15).Select(n => $"VT{n:D3}"), items.Select(i => i.Code));
                Assert.Equal((4, 6, 5), (items.Count(i => i.Group == InputItemGroup.Seed), items.Count(i => i.Group == InputItemGroup.Material), items.Count(i => i.Group == InputItemGroup.Other)));
                Assert.All(items.Where(i => i.Group == InputItemGroup.Seed), i => Assert.Equal("con", i.Unit));
                var proposal = await scope.ServiceProvider.GetRequiredService<FarmingInputItemService>().ProposeAsync(CancellationToken.None);
                Assert.Equal(new NewFarmingInputItemProposal("VT016", "con"), proposal);

                var suppliers = await db.Suppliers.AsNoTracking().ToListAsync();
                Assert.Equal(5, suppliers.Count);
                Assert.All(suppliers, s => Assert.False(string.IsNullOrWhiteSpace(s.Address)));
                Assert.Equal(2, suppliers.Count(s => s.Email is not null));
                Assert.All(suppliers.Where(s => s.Email is not null), s => Assert.EndsWith("@example.com", s.Email));

                // A database seeded before the VT codes: same names, other codes.
                await db.Database.ExecuteSqlRawAsync("UPDATE FarmingInputItems SET Code = N'OLD-' + RIGHT(Code, 3)");
            }

            var again = await RunDemoAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, again.ExitCode);
            using var after = api.Services.CreateScope();
            var check = after.ServiceProvider.GetRequiredService<OmtsDbContext>();
            Assert.Equal(15, await check.FarmingInputItems.CountAsync());
            Assert.False(await check.FarmingInputItems.AnyAsync(i => i.Code.StartsWith("VT")));
            Assert.Equal(20, await check.PurchaseReceipts.CountAsync());
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    // The migrations of this PR are complete: the snapshot equals the model (no "add-migration" left to do).
    [Fact]
    public void The_model_snapshot_matches_the_model()
    {
        using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer("Server=unused;Database=unused").Options);
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        if (snapshot is IMutableModel mutable)
        {
            snapshot = mutable.FinalizeModel();
        }

        snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        var differences = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshot.GetRelationalModel(), db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.Empty(differences);
    }

    /// <summary>
    /// Exactly one 2xx response; every other one is 409 MSG-GEN-02 naming the record it saved and the entry that clashed
    /// (<paramref name="field"/>, also for a loser of the unique index race, QA P1 F2).
    /// </summary>
    private static async Task<Guid> WinnerAsync(IReadOnlyCollection<HttpResponseMessage> responses, HttpStatusCode success, string field)
    {
        var winners = responses.Where(r => r.StatusCode == success).ToList();
        Assert.True(winners.Count == 1, string.Join(", ", await Task.WhenAll(responses.Select(r => r.CodeOrStatusAsync()))));
        var winnerId = (await winners[0].ReadAsync(success)).Id();
        foreach (var loser in responses.Except(winners))
        {
            var problem = await loser.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
            Assert.Equal((winnerId, field), (problem.GetProperty("existingId").GetGuid(), problem.GetProperty("field").GetString()));
        }

        return winnerId;
    }

    private static async Task<string> ProposedAsync(HttpClient owner) =>
        (await (await owner.GetAsync($"{Items}/next-code")).ReadAsync(HttpStatusCode.OK)).GetProperty("code").GetString()!;

    private static string MigrationBefore(DbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        return all[all.FindIndex(m => m.EndsWith("_PurchaseCatalogueV2", StringComparison.Ordinal)) - 1];
    }

    private static Task<int> UniqueIndexCountAsync(DbContext db, string name) =>
        db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = {name} AND is_unique = 1").SingleAsync();

    private static async Task<BootstrapOutcome> RunDemoAsync(SqlServerApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(seedDemoData: true, CancellationToken.None);
    }
}
