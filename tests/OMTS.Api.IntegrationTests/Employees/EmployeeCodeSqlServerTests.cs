using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// EMP-API-06 and EMP-API-01/02 on a real SQL Server: the migration that adds the employee code and its profile fields fills the
/// codes of the employees that exist, and the type filter and sort become SQL.
/// </summary>
public class EmployeeCodeSqlServerTests
{
    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    [SqlServerFact]
    public async Task The_migration_gives_the_existing_employees_codes_in_the_order_they_were_created_and_the_counter_continues()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        var own = all.Single(m => m.EndsWith("_EmployeeCodeAndProfileFields", StringComparison.Ordinal));
        var previous = all[all.IndexOf(own) - 1];
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);

        // By SQL: the model of this migration has columns that the previous schema does not.
        var cooperative = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ({cooperative}, N'HTX', SYSDATETIMEOFFSET())");
        foreach (var (name, createdAt, status) in new[] { ("Ba", "2026-03-01", 402), ("Một", "2026-01-01", 401), ("Hai", "2026-02-01", 401) })
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO Employees (Id, CooperativeId, FullName, StatusId, CreatedAt) VALUES (NEWID(), {cooperative}, {name}, {status}, {createdAt})");
        }

        await migrator.MigrateAsync(own);

        var codes = await db.Database.SqlQueryRaw<string>("SELECT FullName + N'=' + Code AS [Value] FROM Employees ORDER BY Code").ToListAsync();
        Assert.Equal(["Một=NV000001", "Hai=NV000002", "Ba=NV000003"], codes);
        Assert.Equal(3, await ScalarAsync(db, "SELECT CAST(LastValue AS int) AS [Value] FROM DocumentSequences WHERE [Key] = N'NV'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'UX_Employees_Cooperative_Code' AND is_unique = 1 AND has_filter = 1"));
        Assert.False(db.Database.HasPendingModelChanges());

        // The unique index refuses a second employee with a code of the cooperative, whatever its letter case.
        var duplicate = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO Employees (Id, CooperativeId, FullName, StatusId, CreatedAt, Code) VALUES (NEWID(), {cooperative}, N'Trùng', 401, SYSDATETIMEOFFSET(), N'nv000001')"));
        Assert.NotNull(duplicate);

        // Down and up again: the columns, the index and the counter go and come back.
        await migrator.MigrateAsync(previous);
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM DocumentSequences WHERE [Key] = N'NV'"));
        await migrator.MigrateAsync(own);
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Employees WHERE Code IS NOT NULL"));
    }

    [SqlServerFact]
    public async Task New_employees_continue_the_numbers_and_the_type_filter_sort_and_job_type_filter_run_in_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var tag = NewCode();
        var job = new OMTS.Domain.Entities.Labor.JobType { Name = $"Job {tag}" };
        await ChangeAsync(api, db => db.JobTypes.Add(job));
        var a = await CreateAsync(owner, Profile($"{tag} A", NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { job.Id }));
        await CreateAsync(owner, Profile($"{tag} B", null, zone.Id, b => b["employeeType"] = "Staff"));
        // Staff from a future date: today still a seasonal worker, as TypeOn reads it.
        var c = await CreateAsync(owner, Profile($"{tag} C", null, zone.Id));
        var version = RowVersionOf(c);
        var future = await owner.PutAsJsonAsync($"/api/employees/{IdOf(c)}", Profile($"{tag} C", null, zone.Id, x =>
        {
            x["employeeType"] = "Staff";
            x["employeeTypeFrom"] = EmployeeRequests.Today().AddDays(10).ToString("yyyy-MM-dd");
            x["rowVersion"] = version;
        }));
        Assert.Equal(HttpStatusCode.OK, future.StatusCode);

        async Task<string[]> NamesAsync(string query)
        {
            var page = await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/employees?search={tag}&{query}");
            return page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fullName").GetString()!).ToArray();
        }

        Assert.Equal([$"{tag} B"], await NamesAsync("employeeType=Staff"));
        Assert.Equal([$"{tag} A", $"{tag} C"], await NamesAsync("employeeType=SeasonalWorker&sortBy=name"));
        Assert.Equal([$"{tag} A"], await NamesAsync($"jobTypeId={job.Id}"));
        Assert.Equal($"{tag} B", (await NamesAsync("sortBy=type&sortDir=desc"))[0]);
        Assert.Equal($"{tag} B", (await NamesAsync("sortBy=type"))[^1]);
        Assert.Equal([$"{tag} C", $"{tag} B", $"{tag} A"], await NamesAsync("sortBy=code&sortDir=desc"));
        Assert.Equal($"{tag} C", (await NamesAsync("sortBy=phone"))[0]);
        // The code of the first employee is the first number of the counter after the codes the migration gave: NV000001 on a new database.
        Assert.Equal("NV000001", a.GetProperty("employee").GetProperty("code").GetString());
        // Search by code is case-insensitive on SQL Server.
        Assert.Equal(1, (await owner.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/employees?search=nv000001")).GetProperty("total").GetInt32());
    }

    [SqlServerFact]
    public async Task A_typed_code_is_unique_whatever_its_letter_case_on_sql_server()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        await CreateAsync(owner, Profile($"Typed {NewCode()}", null, zone.Id, b => b["code"] = "ABC-1"));

        var again = await owner.PostAsJsonAsync("/api/employees", Profile($"Typed {NewCode()}", null, zone.Id, b => b["code"] = "abc-1"));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }
}
