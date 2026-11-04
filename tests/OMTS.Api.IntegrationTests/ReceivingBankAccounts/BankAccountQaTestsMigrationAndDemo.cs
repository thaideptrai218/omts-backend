using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.ReceivingBankAccounts.ReceivingBankAccountTestKit;

namespace OMTS.Api.IntegrationTests.ReceivingBankAccounts;

/// <summary>QA of migration 20261104184513 (Up, Down, Up with its hand-written SQL) and of the demo step on a real SQL Server.</summary>
public sealed class BankAccountQaTestsMigrationAndDemo
{
    private const string Demo = "DEMO00000001";

    [SqlServerFact]
    public async Task Up_down_up_adds_only_the_two_columns_and_leaves_employees_alone()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var previous = PreviousMigration(db);

        await migrator.MigrateAsync(previous);
        var cooperatives = await ColumnsAsync(database, "Cooperatives");
        var employees = await ColumnsAsync(database, "Employees");

        await db.Database.MigrateAsync();
        var added = (await ColumnsAsync(database, "Cooperatives")).Except(cooperatives).Order().ToList();

        Assert.Equal(["BankAccount_AcceptedAt datetimeoffset -1 YES", "BankAccount_BankBin nvarchar 6 YES"], added);
        Assert.Empty(cooperatives.Except(await ColumnsAsync(database, "Cooperatives")));
        Assert.Equal(employees, await ColumnsAsync(database, "Employees"));
        Assert.False(db.Database.HasPendingModelChanges());

        await migrator.MigrateAsync(previous);
        Assert.Equal(cooperatives, await ColumnsAsync(database, "Cooperatives"));
        Assert.Equal(employees, await ColumnsAsync(database, "Employees"));

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [SqlServerFact]
    public async Task A_saved_account_is_cleared_by_down_and_up_and_can_be_set_again_while_an_employee_bank_account_survives()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var employee = IdOf(await CreateAsync(owner, Profile("Nhân viên QA", AcsTestKit.NewPhone(), zone.Id, b => b["bankAccount"] = Bank("000000000077"))));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)))).StatusCode);

        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration(db));
        // Down keeps the three old columns, so the old schema still reads a whole account.
        Assert.Equal(FirstNumber, await ScalarAsync(database, "SELECT BankAccount_AccountNumber FROM Cooperatives"));

        await db.Database.MigrateAsync();

        Assert.Equal(JsonValueKind.Null, (await AccountAsync(owner)).ValueKind);
        Assert.Equal(DBNull.Value, await ScalarAsync(database, "SELECT BankAccount_BankName FROM Cooperatives"));
        Assert.Equal(DBNull.Value, await ScalarAsync(database, "SELECT BankAccount_AccountHolderName FROM Cooperatives"));
        Assert.Equal("000000000077", await QueryAsync(api, c => c.Employees.AsNoTracking().Where(e => e.Id == employee).Select(e => e.BankAccount!.AccountNumber).SingleAsync()));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(Route, Body(await RowVersionAsync(owner)))).StatusCode);
    }

    [SqlServerFact]
    public async Task The_up_of_a_row_with_a_partial_old_account_leaves_no_half_account()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration(db));
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO Cooperatives (Id, FullName, StatusId, CreatedAt, BankAccount_AccountNumber) VALUES (NEWID(), N'HTX', {StatusIds.Cooperative.Draft}, SYSDATETIMEOFFSET(), N'TESTPARTIAL1')");

        await db.Database.MigrateAsync();

        Assert.Null((await db.Cooperatives.AsNoTracking().SingleAsync()).BankAccount);
    }

    [SqlServerFact]
    public async Task Demo_reruns_add_nothing_ask_the_service_once_and_keep_what_the_owner_set()
    {
        var service = ScriptedPaymentService.Answering(AccountAcceptanceResult.Accepted);
        await using var database = SqlServerTestDatabase.Create();
        await using var api = NewDemoFactory(database, out _, out var storage);
        await using var app = api.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddSingleton<IPaymentNotificationService>(service)));
        try
        {
            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(app)).ExitCode);
            Assert.Equal(1, service.Calls);
            var demo = await QueryAsync(api, db => db.Cooperatives.AsNoTracking().Select(c => c.BankAccount).SingleAsync());
            Assert.Equal((Vietcombank, Demo), (demo!.BankBin, demo.AccountNumber));
            var audit = await QueryAsync(api, db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction));
            var ownerId = await QueryAsync(api, db => db.Accounts.Where(a => a.SubjectType == SubjectType.CooperativeOwner).Select(a => a.Id).SingleAsync());
            Assert.Equal(ownerId, audit.ActorAccountId);

            var again = await RunAsync(app);
            Assert.Contains("Demo receiving bank account: already there.", again.Messages);
            Assert.Equal(1, service.Calls);
            Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == AuditAction)));

            await QueryAsync(api, db => db.Database.ExecuteSqlRawAsync($"UPDATE Cooperatives SET BankAccount_AccountNumber = N'{FirstNumber}'"));
            var thirdRun = await RunAsync(app);
            Assert.Equal(DeploymentBootstrapper.Success, thirdRun.ExitCode);
            Assert.Equal(FirstNumber, (await QueryAsync(api, db => db.Cooperatives.AsNoTracking().Select(c => c.BankAccount).SingleAsync()))!.AccountNumber);
        }
        finally
        {
            Remove(storage);
        }
    }

    // README: None is the default and "no receiving bank account can be accepted". The demo needs a working Development
    // whatever provider is set, and the same holds when a real provider later refuses the fictional account.
    [SqlServerFact]
    public async Task L4_the_demo_data_is_seeded_in_development_with_no_payment_provider()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = NewDemoFactory(database, out _, out var storage);
        await using var app = api.WithWebHostBuilder(b => b.UseSetting("Payments:Provider", "None"));
        try
        {
            var outcome = await RunAsync(app);

            Assert.Equal(DeploymentBootstrapper.Success, outcome.ExitCode);
            Assert.True(await QueryAsync(api, db => db.FarmingZones.AnyAsync()));
            Assert.Contains(outcome.Messages, m => m.StartsWith("Demo receiving bank account: skipped", StringComparison.Ordinal) && m.Contains("MSG-DEBT-02"));
            Assert.Null(await QueryAsync(api, db => db.Cooperatives.AsNoTracking().Select(c => c.BankAccount).SingleAsync()));
        }
        finally
        {
            Remove(storage);
        }
    }

    private static string PreviousMigration(OmtsDbContext db)
    {
        var migrations = db.Database.GetMigrations().ToList();
        return migrations[migrations.FindIndex(m => m.EndsWith("_ReceivingBankAccount", StringComparison.Ordinal)) - 1];
    }

    private static async Task<List<string>> ColumnsAsync(SqlServerTestDatabase database, string table)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COLUMN_NAME + ' ' + DATA_TYPE + ' ' + CAST(COALESCE(CHARACTER_MAXIMUM_LENGTH, -1) AS varchar(12)) + ' ' + IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @table ORDER BY COLUMN_NAME";
        command.Parameters.AddWithValue("@table", table);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static async Task<object?> ScalarAsync(SqlServerTestDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static SqlServerApiFactory NewDemoFactory(SqlServerTestDatabase database, out string password, out string storage)
    {
        password = AcsTestKit.NewPassword();
        storage = Path.Combine(Path.GetTempPath(), $"omts-qa-bank-{Guid.NewGuid():N}");
        return new SqlServerApiFactory(database, new Dictionary<string, string?>
        {
            [BootstrapSettings.CooperativeFullNameKey] = "Hợp tác xã QA",
            [BootstrapSettings.OwnerDisplayNameKey] = "Chủ nhiệm QA",
            [BootstrapSettings.OwnerUsernameKey] = "owner",
            [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
            [BootstrapSettings.OwnerTemporaryPasswordKey] = password,
            ["Storage:Local:RootPath"] = storage,
        });
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }

    private static void Remove(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
