using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace OMTS.Api.IntegrationTests.Persistence;

/// <summary>
/// QA of the merge of the purchase stack into the raft and cycle stack: the migration chain interleaves both by timestamp. It must apply on an empty database, build the schema
/// of the model, and go down to any point inside the interleave and up again, also with data in every table.
/// </summary>
public class StackMergeQaTestsMigrations
{
    /// <summary>The migrations the merge interleaves, in chain order, from the first that either side added.</summary>
    private static readonly string[] Interleave =
    [
        "20261104071927_FixedRoles", "20261104072127_NotificationsV2", "20261104072225_PurchaseCatalogueV2", "20261104072358_CooperativeProfileV2", "20261104072437_ZonesLicensesV2",
        "20261104072522_PurchaseReceiptsV2", "20261104084901_SupplierPaymentsV2", "20261104100119_PurchaseReturnsV2", "20261104103030_RaftsV2", "20261104113119_CultivationCyclesV2",
        "20261104124405_CultivationLogs", "20261104164709_MaterialUsages", "20261104182051_SeedingRecords",
    ];

    private const string BeforeTheStack = "20261028151320_RemoveLegacyScaffolding";

    private static readonly string[] GoneTables =
        ["SeedReceiptLots", "SeedingEvents", "LaneSegments", "CultivationLogEntries", "RaftLayoutVersions", "ZoneComplianceDocumentZones"];

    private const string SchemaQuery = """
        SELECT CONCAT('table ', t.name) AS [Value] FROM sys.tables t WHERE t.name <> '__EFMigrationsHistory'
        UNION ALL SELECT CONCAT('column ', t.name, '.', c.name, ' ', ty.name, '(', c.max_length, ',', c.precision, ',', c.scale, ') null=', c.is_nullable)
            FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id WHERE t.name <> '__EFMigrationsHistory'
        UNION ALL SELECT CONCAT('index ', t.name, '.', i.name, ' unique=', i.is_unique, ' filter=', i.filter_definition, ' on ',
                (SELECT STRING_AGG(CONCAT(col.name, CASE WHEN ic.is_included_column = 1 THEN '+' ELSE '' END), ',') WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, col.name)
                 FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id))
            FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE i.type > 0 AND t.name <> '__EFMigrationsHistory'
        UNION ALL SELECT CONCAT('fk ', OBJECT_NAME(f.parent_object_id), ' -> ', OBJECT_NAME(f.referenced_object_id), ' ', f.name, ' delete=', f.delete_referential_action) FROM sys.foreign_keys f
        UNION ALL SELECT CONCAT('check ', OBJECT_NAME(c.parent_object_id), ' ', c.name, ' ', c.definition) FROM sys.check_constraints c
        """;

    private const string CatalogQuery = """
        SELECT CONCAT('status ', Id, '|', Code, '|', [Group], '|', DisplayName, '|', IsInitial, '|', IsFinal, '|', SortOrder, '|', Meaning) AS [Value] FROM Statuses
        UNION ALL SELECT CONCAT('transition ', Id, '|', FromStatusId, '|', ToStatusId, '|', UseCases, '|', AllowedPerformers, '|', TriggerEvent, '|', ReasonRequired, '|', GuardCondition) FROM StatusTransitions
        """;

    private static async Task<List<string>> ReadAsync(OmtsDbContext db, string query) =>
        (await db.Database.SqlQueryRaw<string>(query).ToListAsync()).Order(StringComparer.Ordinal).ToList();

    private static void AssertSameLines(IReadOnlyCollection<string> expected, IReadOnlyCollection<string> actual, string what)
    {
        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0, $"{what}\nOnly in the model:\n{string.Join("\n", missing)}\nOnly in the migrations:\n{string.Join("\n", extra)}");
    }

    // The chain on an empty database gives the same tables, columns, indexes, keys and checks, and the same status rows, as the model the snapshot claims.
    [SqlServerFact]
    public async Task The_chain_applies_on_an_empty_database_and_gives_the_schema_and_catalog_of_the_model()
    {
        await using var migrated = SqlServerTestDatabase.Create();
        await using var modelled = SqlServerTestDatabase.Create();
        await using var chain = migrated.CreateContext();
        await using var model = modelled.CreateContext();

        await chain.Database.MigrateAsync();
        await model.Database.EnsureCreatedAsync();

        Assert.Empty(await chain.Database.GetPendingMigrationsAsync());
        Assert.False(chain.Database.HasPendingModelChanges());
        Assert.Equal(Interleave, (await chain.Database.GetAppliedMigrationsAsync()).TakeLast(Interleave.Length));
        AssertSameLines(await ReadAsync(model, SchemaQuery), await ReadAsync(chain, SchemaQuery), "schema");
        AssertSameLines(await ReadAsync(model, CatalogQuery), await ReadAsync(chain, CatalogQuery), "status catalog");

        var tables = await chain.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables").ToListAsync();
        Assert.Empty(GoneTables.Intersect(tables));
    }

    // Step by step from the head to before the stack and up again: each Down leaves what the Up before it expects.
    [SqlServerFact]
    public async Task Each_step_of_the_interleave_goes_down_on_an_empty_schema_and_the_whole_chain_comes_up_the_same()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        var head = await ReadAsync(db, SchemaQuery);
        var catalog = await ReadAsync(db, CatalogQuery);

        foreach (var target in Interleave.Reverse().Skip(1).Append(BeforeTheStack))
        {
            await migrator.MigrateAsync(target);
            Assert.Equal(target, (await db.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Empty((await ReadAsync(db, CatalogQuery)).Where(l => l.Contains("|PURCHASE_RETURN|", StringComparison.Ordinal) && string.CompareOrdinal(target, "20261104100119_PurchaseReturnsV2") < 0));
        }

        await migrator.MigrateAsync();

        AssertSameLines(head, await ReadAsync(db, SchemaQuery), "schema after down and up");
        AssertSameLines(catalog, await ReadAsync(db, CatalogQuery), "status catalog after down and up");
    }

    // The same with a demo database: rafts, lanes, cycles, logs, material usage, receipts, returns, payments and seeding sessions in the tables each Down changes.
    [SqlServerFact]
    public async Task A_database_with_the_demo_data_goes_down_to_every_point_of_the_interleave_and_up_again()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-merge-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using (var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "HTX QA",
                [BootstrapSettings.OwnerDisplayNameKey] = "Owner QA",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
                ["Storage:Local:RootPath"] = storageRoot,
            }))
            {
                using var scope = api.Services.CreateScope();
                Assert.Equal(DeploymentBootstrapper.Success, (await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None)).ExitCode);
            }

            await using var db = database.CreateContext();
            var migrator = db.GetService<IMigrator>();
            var (rafts, receipts) = (await db.Rafts.CountAsync(), await db.PurchaseReceipts.CountAsync());
            Assert.True(await db.SeedingRecords.AnyAsync() && await db.PurchaseReturns.AnyAsync() && await db.MaterialUsages.AnyAsync() && await db.CultivationLogs.AnyAsync());
            var schema = await ReadAsync(db, SchemaQuery);

            foreach (var target in Interleave.Reverse().Skip(1))
            {
                try
                {
                    await migrator.MigrateAsync(target);
                }
                catch (SqlException failure)
                {
                    Assert.Fail($"Down to {target} failed on the demo data: {failure.Message}");
                }

                // Every step leaves the rafts and the receipts: Down is lossy on the new tables only.
                Assert.Equal(rafts, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Rafts").SingleAsync());
                Assert.Equal(receipts, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM PurchaseReceipts").SingleAsync());
            }

            await migrator.MigrateAsync();

            db.ChangeTracker.Clear();
            AssertSameLines(schema, await ReadAsync(db, SchemaQuery), "schema after down and up");
            Assert.Equal((rafts, receipts), (await db.Rafts.CountAsync(), await db.PurchaseReceipts.CountAsync()));
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    // Each side's chain, applied alone, then the merged code: EF runs the migrations of the other side out of timestamp order. A database made on either PR must still reach the head.
    [SqlServerFact]
    public Task A_database_made_on_the_raft_side_reaches_the_head_and_the_schema_of_the_model() => SideReachesTheHeadAsync(
        "raft and cycle side",
        "20261104071927_FixedRoles,20261104072127_NotificationsV2,20261104072358_CooperativeProfileV2,20261104072437_ZonesLicensesV2,20261104103030_RaftsV2,20261104113119_CultivationCyclesV2,20261104124405_CultivationLogs,20261104164709_MaterialUsages");

    [SqlServerFact]
    public Task A_database_made_on_the_purchase_side_reaches_the_head_and_the_schema_of_the_model() => SideReachesTheHeadAsync(
        "purchase side",
        "20261104071927_FixedRoles,20261104072127_NotificationsV2,20261104072225_PurchaseCatalogueV2,20261104072522_PurchaseReceiptsV2,20261104084901_SupplierPaymentsV2,20261104100119_PurchaseReturnsV2");

    private static async Task SideReachesTheHeadAsync(string side, string migrations)
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(BeforeTheStack);

        foreach (var name in migrations.Split(','))
        {
            var script = migrator.GenerateScript(all[all.IndexOf(name) - 1], name, MigrationsSqlGenerationOptions.NoTransactions);
            foreach (var batch in script.Split(["\r\nGO\r\n", "\nGO\n", "\r\nGO", "\nGO"], StringSplitOptions.RemoveEmptyEntries).Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                await db.Database.ExecuteSqlRawAsync(batch);
            }
        }

        try
        {
            await migrator.MigrateAsync();
        }
        catch (SqlException failure)
        {
            Assert.Fail($"A database of the {side} does not reach the head: {failure.Message}");
        }

        await using var modelled = SqlServerTestDatabase.Create();
        await using var model = modelled.CreateContext();
        await model.Database.EnsureCreatedAsync();
        AssertSameLines(await ReadAsync(model, SchemaQuery), await ReadAsync(db, SchemaQuery), $"schema from the {side}");
        AssertSameLines(await ReadAsync(model, CatalogQuery), await ReadAsync(db, CatalogQuery), $"status catalog from the {side}");
    }

    // The catalog holds both sides with no collision and transitions that stay inside a status group, each named once and never out of a final status.
    [SqlServerFact]
    public async Task The_status_catalog_holds_both_sides_and_its_transitions_stay_consistent()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();

        var statuses = await db.Statuses.AsNoTracking().ToListAsync();
        var transitions = await db.StatusTransitions.AsNoTracking().ToListAsync();

        Assert.Equal(statuses.Count, statuses.Select(s => s.Id).Distinct().Count());
        Assert.Equal(statuses.Count, statuses.Select(s => (s.Group, s.Code)).Distinct().Count());
        foreach (var group in statuses.GroupBy(s => s.Group))
        {
            Assert.True(group.Count(s => s.IsInitial) == 1, $"group {group.Key} has {group.Count(s => s.IsInitial)} initial statuses");
            Assert.Equal(group.Count(), group.Select(s => s.SortOrder).Distinct().Count());
            Assert.Single(group.Select(s => s.Id / 100).Distinct());
        }

        var byId = statuses.ToDictionary(s => s.Id);
        foreach (var transition in transitions)
        {
            Assert.Equal(byId[transition.FromStatusId].Group, byId[transition.ToStatusId].Group);
            Assert.False(byId[transition.FromStatusId].IsFinal, $"transition {transition.Id} leaves a final status");
            Assert.False(string.IsNullOrWhiteSpace(transition.UseCases));
        }

        Assert.Equal(transitions.Count, transitions.Select(t => (t.FromStatusId, t.ToStatusId)).Distinct().Count());

        // Both sides are there, and the Draft is removed rather than cancelled: transition 71 went away with the purchase side.
        Assert.Contains(statuses, s => s.Id == 1901 && s.Group == "PURCHASE_RETURN");
        Assert.Contains(statuses, s => s.Id == 2001 && s.Group == "LANE");
        Assert.Contains(transitions, t => t.Id == 74);
        Assert.Contains(transitions, t => t.Id == 76);
        Assert.DoesNotContain(transitions, t => t.Id == 71);
    }

    // SeedingRecords drops SeedingEvents: rows there do not stop the Up, and Down gives back an empty old table with the schema it had (client list: no screen wrote it).
    [SqlServerFact]
    public async Task SeedingRecords_goes_up_over_rows_in_the_old_table_and_down_and_up_again()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261104164709_MaterialUsages");
        var oldSchema = await ReadAsync(db, SchemaQuery);
        var cycle = new CultivationCycle { Raft = new Raft { Zone = new FarmingZone { Cooperative = new Cooperative { FullName = "HTX" }, Code = "KV01", Name = "A" }, Code = "B01", Size = "1" }, StartDate = new DateOnly(2026, 1, 1) };
        var account = new Account { Username = "owner", DisplayName = "Owner", SubjectType = SubjectType.CooperativeOwner, StatusId = StatusIds.Account.Active };
        var item = new FarmingInputItem { Code = "VT001", Name = "Seed", Group = InputItemGroup.Seed, Unit = "con" };
        var receipt = new PurchaseReceipt { Code = "PN000001", Supplier = new Supplier { Name = "S", PhoneNumber = "0900000002" }, ReceiptDate = new DateOnly(2026, 1, 1), RecordedByAccountId = Guid.Empty, StatusId = StatusIds.PurchaseReceipt.Completed };
        db.AddRange(cycle, account, item, receipt);
        receipt.RecordedByAccountId = account.Id;
        var line = new PurchaseReceiptLine { Receipt = receipt, LineNumber = 1, Item = item, Unit = "con", Quantity = 10, UnitPrice = 1 };
        db.Add(line);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlAsync($"INSERT INTO SeedingEvents (Id, CycleId, SeedLotId, SeedingDate, RecordedByAccountId, EvidenceUrls, CreatedAt) VALUES (NEWID(), {cycle.Id}, {line.Id}, '2026-01-02', {account.Id}, N'[]', SYSDATETIMEOFFSET())");
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync();

        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables").ToListAsync();
        Assert.DoesNotContain("SeedingEvents", tables);
        Assert.Contains("SeedingRecords", tables);
        Assert.False(db.Database.HasPendingModelChanges());

        await migrator.MigrateAsync("20261104164709_MaterialUsages");
        AssertSameLines(oldSchema, await ReadAsync(db, SchemaQuery), "schema after Down");
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM SeedingEvents").SingleAsync());

        await migrator.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
