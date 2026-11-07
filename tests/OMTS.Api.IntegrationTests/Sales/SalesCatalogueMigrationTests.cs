using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>The migration SalesCatalogueV2 on a real SQL Server.</summary>
public class SalesCatalogueMigrationTests
{
    private const string MigrationBefore = "20261104084530_AccountRulesV2";
    // Data needs of the schema: a repeated name, the required price, the moved address; and Down restores the old columns.
    [SqlServerFact]
    public async Task The_migration_keeps_existing_rows_and_reverts()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        // By SQL: later migrations add columns of the model that this one does not have yet.
        var cooperative = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ({cooperative}, N'HTX', SYSDATETIMEOFFSET())");
        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("@cooperative", cooperative);
            command.CommandText =
                """
                INSERT INTO Products (Id, Code, Name, SalesUnit, StatusId, CreatedAt) VALUES
                    ('00000000-0000-0000-0000-000000000001', N'P1', N'Hàu', N'kg', 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000002', N'P2', N'Hàu', N'kg', 102, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000003', N'P3', N'Hàu sữa', N'kg', 101, SYSDATETIMEOFFSET());
                INSERT INTO Customers (Id, CooperativeId, Name, PhoneNumber, DeliveryAddress, StatusId, CreatedAt) VALUES
                    ('00000000-0000-0000-0000-000000000011', @cooperative, N'A', N'0900000091', N'Hạ Long', 101, SYSDATETIMEOFFSET()),
                    ('00000000-0000-0000-0000-000000000012', @cooperative, N'B', N'0900000091', NULL, 101, SYSDATETIMEOFFSET());
                """;
            await command.ExecuteNonQueryAsync();
        }

        await db.Database.MigrateAsync();

        var products = await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(["Hàu (P1)", "Hàu (P2)", "Hàu sữa"], products.Select(p => p.Name));
        Assert.All(products, p => Assert.Equal(1m, p.UnitPrice));
        var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(("Hạ Long", "0900000091", false), (customers[0].Address, customers[1].PhoneNumber, customers[0].PhoneCheckedByStaff));

        await migrator.MigrateAsync(MigrationBefore);
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
