using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The migration of the customer module over rows written before it: every profile gets its code in the order it was created and the KH sequence goes on after them, every
/// payment gets the time it happened (the start of its date in Vietnam), profiles are In Use, and the model has no change left; down and up again keep the rows.
/// </summary>
public class CustomerProfileMigrationSqlServerTests
{
    private const string MigrationBefore = "20261110060643_CashBookVouchersAccountsAndTypes";
    private const string Migration = "20261110132829_CustomerProfilePaymentsAllocationsAndAdjustments";

    [SqlServerFact]
    public async Task Existing_profiles_get_codes_oldest_first_and_existing_payments_the_start_of_their_day_in_vietnam()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToArray();
        Assert.Equal(MigrationBefore, all[Array.IndexOf(all, Migration) - 1]);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Customers NOCHECK CONSTRAINT ALL");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Customers (Id, CooperativeId, Name, PhoneCheckedByStaff, CreatedAt) VALUES "
            + "('00000000-0000-0000-0000-00000000000b', NEWID(), 'Second', 0, '2026-11-02T08:00:00+00:00'), "
            + "('00000000-0000-0000-0000-00000000000c', NEWID(), 'Third', 0, '2026-11-03T08:00:00+00:00'), "
            + "('00000000-0000-0000-0000-00000000000a', NEWID(), 'First', 0, '2026-11-01T08:00:00+00:00')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO CustomerPayments (Id, Code, CustomerId, Kind, PaymentDate, Amount, Method, StatusId, CreatedAt) VALUES "
            + "(NEWID(), 'PT900001', NULL, 'Payment', '2026-11-07', 300000, 'Cash', 3601, SYSDATETIMEOFFSET())");

        await migrator.MigrateAsync();

        var customers = await db.Database.SqlQueryRaw<string>("SELECT CONCAT(Code, ' ', Name, ' ', StatusId, ' ', CustomerType) AS [Value] FROM Customers ORDER BY Code").ToListAsync();
        Assert.Equal([$"KH000001 First {StatusIds.Usage.InUse} Individual", $"KH000002 Second {StatusIds.Usage.InUse} Individual", $"KH000003 Third {StatusIds.Usage.InUse} Individual"], customers);
        Assert.Equal(3, await db.Database.SqlQueryRaw<long>("SELECT LastValue AS [Value] FROM DocumentSequences WHERE [Key] = 'KH'").SingleAsync());
        var paidAt = await db.Database.SqlQueryRaw<string>("SELECT CONVERT(varchar(40), PaidAt, 127) AS [Value] FROM CustomerPayments").SingleAsync();
        Assert.Equal("2026-11-07T00:00:00+07:00", paidAt);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Statuses WHERE Id = 3604 AND Code = 'PENDING_TRANSFER'").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM CashVoucherTypes WHERE Id = '5d6c1a10-0000-4c3a-9a10-000000000008'").SingleAsync());
        Assert.False(db.Database.HasPendingModelChanges());

        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM Customers").SingleAsync());
        await migrator.MigrateAsync();
        Assert.Equal("KH000001", await db.Database.SqlQueryRaw<string>("SELECT Code AS [Value] FROM Customers WHERE Name = 'First'").SingleAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
