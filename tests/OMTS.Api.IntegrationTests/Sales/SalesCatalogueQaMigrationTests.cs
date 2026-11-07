using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: the migration SalesCatalogueV2 Up, Down, Up on data of the shape a demo database holds.</summary>
public class SalesCatalogueQaMigrationTests
{
    private const string MigrationBefore = "20261104084530_AccountRulesV2";

    private static async Task SqlAsync(SqlServerTestDatabase database, string text, Guid? cooperative = null)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        if (cooperative is { } id)
        {
            command.Parameters.AddWithValue("@cooperative", id);
        }

        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqlServerTestDatabase database, string text)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<(OmtsDbContext Db, IMigrator Migrator, Guid Cooperative)> BeforeAsync(SqlServerTestDatabase database)
    {
        var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        // By SQL: later migrations add columns of the model that this one does not have yet.
        var cooperative = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ({cooperative}, N'HTX', SYSDATETIMEOFFSET())");
        return (db, migrator, cooperative);
    }

    // The unique index compares with the collation: every spelling that collides gets its code, and a name at the limit still fits.
    [SqlServerFact]
    public async Task Up_gives_every_colliding_spelling_its_code_and_stays_within_the_name_limit()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (db, _, _) = await BeforeAsync(database);
        await using (db)
        {
            var longName = new string('H', 200);
            var longCode = new string('C', 50);
            await SqlAsync(database, $"""
                INSERT INTO Products (Id, Code, Name, SalesUnit, StatusId, CreatedAt) VALUES
                    ('00000000-0000-0000-0000-000000000001', N'P1', N'Hàu sữa', N'kg', 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000002', N'P2', N'HÀU SỮA', N'kg', 102, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000003', N'P3', N'hàu sữa ', N'kg', 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000004', N'{longCode}', N'{longName}', N'kg', 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000005', N'P5', N'{longName}', N'kg', 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000006', N'P6', N'Hàu béo', N'kg', 101, SYSDATETIMEOFFSET());
                """);

            await db.Database.MigrateAsync();

            var products = await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
            Assert.Equal(6, products.Count);
            Assert.Equal(6, products.Select(p => p.Name.ToUpperInvariant().Trim()).Distinct().Count());
            Assert.Equal("Hàu béo", products[5].Name);
            Assert.All(products, p => Assert.True(p.Name.Length <= 200));
            Assert.All(products, p => Assert.Equal(1m, p.UnitPrice));
            Assert.EndsWith($" ({longCode})", products[3].Name);
            Assert.Equal(StatusIds.Usage.Discontinued, products[1].StatusId);
        }
    }

    // A third row that already holds the name the conversion makes: the migration must still finish.
    [SqlServerFact]
    public async Task L4_up_finishes_when_a_product_already_has_the_name_the_conversion_makes()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (db, _, _) = await BeforeAsync(database);
        await using (db)
        {
            await SqlAsync(database, """
                INSERT INTO Products (Id, Code, Name, SalesUnit, StatusId, CreatedAt) VALUES
                    ('00000000-0000-0000-0000-000000000001', N'P1', N'Hàu', N'kg', 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000002', N'P2', N'Hàu', N'kg', 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000003', N'P3', N'Hàu (P1)', N'kg', 101, SYSDATETIMEOFFSET());
                """);

            await db.Database.MigrateAsync();

            Assert.Equal(3, await db.Products.CountAsync());
        }
    }

    // Rows the old columns held: the address moves, the rest goes (dev and demo data only), a repeated phone stays until merged.
    [SqlServerFact]
    public async Task Up_keeps_every_customer_row_with_its_merge_link_and_repeated_numbers()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (db, _, cooperative) = await BeforeAsync(database);
        await using (db)
        {
            await SqlAsync(database, """
                INSERT INTO Customers (Id, CooperativeId, Name, PhoneNumber, OtherContacts, DeliveryAddress, ReceivingContact, StatusId, CreatedAt) VALUES
                    ('00000000-0000-0000-0000-000000000011', @cooperative, N'A', N'0900000091', N'zalo', N'Hạ Long', N'Chị A', 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000012', @cooperative, N'B', N'0900000091', NULL, NULL, NULL, 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000013', @cooperative, N'C', N'0900000092', NULL, NULL, NULL, 102, SYSDATETIMEOFFSET());
                INSERT INTO Customers (Id, CooperativeId, Name, PhoneNumber, MergedIntoCustomerId, StatusId, CreatedAt) VALUES
                    ('00000000-0000-0000-0000-000000000014', @cooperative, N'D', N'0900000092', '00000000-0000-0000-0000-000000000011', 101, SYSDATETIMEOFFSET());
                """, cooperative);

            await db.Database.MigrateAsync();

            var rows = await db.Customers.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
            Assert.Equal(4, rows.Count);
            Assert.Equal("Hạ Long", rows[0].Address);
            Assert.All(rows, r => Assert.False(r.PhoneCheckedByStaff));
            Assert.Equal(rows[0].Id, rows[3].MergedIntoCustomerId);
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(database, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_Customers_PhoneNumber_CheckedByStaff' AND has_filter = 1 AND is_unique = 1")));
        }
    }

    // Up, Down, Up with rows the new services wrote: everything restores and the indexes hold; prices start again at 1 VND.
    [SqlServerFact]
    public async Task Down_and_up_again_on_rows_written_since_keep_the_rows_and_leave_no_pending_change()
    {
        await using var database = SqlServerTestDatabase.Create();
        var (db, migrator, cooperative) = await BeforeAsync(database);
        await using (db)
        {
            await db.Database.MigrateAsync();
            var kept = new Customer { CooperativeId = cooperative, Name = "Kept", PhoneNumber = "0900000093", Address = "Vân Đồn", Note = "Sáng" };
            kept.PhoneCheckedByStaff = true;
            var merged = new Customer { CooperativeId = cooperative, Name = "Merged", PhoneNumber = "0900000093", MergedIntoCustomerId = kept.Id };
            merged.PhoneCheckedByStaff = true;
            db.Customers.AddRange(kept, merged);
            db.Products.AddRange(new Product { Name = "Hàu sữa", UnitPrice = 60_000 }, new Product { Name = "Hàu béo", UnitPrice = 75_000, StatusId = StatusIds.Usage.Discontinued });
            await db.SaveChangesAsync();

            await migrator.MigrateAsync(MigrationBefore);

            Assert.Equal(2, Convert.ToInt32(await ScalarAsync(database, "SELECT COUNT(*) FROM Products WHERE Code <> '' AND SalesUnit = 'kg'")));
            Assert.Equal(2, Convert.ToInt32(await ScalarAsync(database, "SELECT COUNT(*) FROM Customers WHERE StatusId = 101")));
            Assert.Equal("Vân Đồn", await ScalarAsync(database, "SELECT DeliveryAddress FROM Customers WHERE Name = N'Kept'"));

            await db.Database.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            var rows = await db.Customers.AsNoTracking().ToListAsync();
            Assert.Equal(kept.Id, rows.Single(r => r.Name == "Merged").MergedIntoCustomerId);
            Assert.Equal("Vân Đồn", rows.Single(r => r.Name == "Kept").Address);
            var products = await db.Products.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
            Assert.Equal(["Hàu béo", "Hàu sữa"], products.Select(p => p.Name));
            Assert.Equal(StatusIds.Usage.Discontinued, products[0].StatusId);
        }
    }
}
