using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Workforce;

public sealed class MiniAppSchemaMergeSqlTests
{
    [SqlServerFact]
    public async Task Existing_mobile_requests_staff_replies_work_attendance_and_payroll_locks_survive_the_develop_merge()
    {
        await using var database = SqlServerTestDatabase.Create();
        var options = new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(database.ConnectionString)
            .ReplaceService<IMigrationsAssembly, LegacyMiniAppAssembly>().Options;
        await using (var legacy = new OmtsDbContext(options)) await legacy.Database.MigrateAsync();

        await using var sql = new SqlConnection(database.ConnectionString);
        await sql.OpenAsync();
        var day = new DateTime(2030, 1, 2);
        var owner = await InsertAsync(sql, "Accounts", new() { ["SubjectType"] = "CooperativeOwner", ["StatusId"] = 301 });
        var staff = await InsertAsync(sql, "Accounts", new() { ["SubjectType"] = "Staff", ["StatusId"] = 301 });
        var trader = await InsertAsync(sql, "Accounts", new() { ["SubjectType"] = "Trader", ["StatusId"] = 301 });
        var coop = await InsertAsync(sql, "Cooperatives", new() { ["StatusId"] = 201 });
        var employee = await InsertAsync(sql, "Employees", new() { ["CooperativeId"] = coop, ["AccountId"] = staff, ["StatusId"] = 401 });
        var zone = await InsertAsync(sql, "FarmingZones", new() { ["CooperativeId"] = coop, ["StatusId"] = 101 });
        var raft = await InsertAsync(sql, "Rafts", new() { ["ZoneId"] = zone, ["StatusId"] = 101 });
        var job = await InsertAsync(sql, "JobType", new() { ["StatusId"] = 101 });
        var customer = await InsertAsync(sql, "Customers", new() { ["CooperativeId"] = coop, ["AccountId"] = trader, ["StatusId"] = 101 });
        var product = await InsertAsync(sql, "Products", new() { ["StatusId"] = 101, ["SalesUnit"] = "Kg", ["Code"] = "LEGACY-PRODUCT" });
        var request = await InsertAsync(sql, "PurchaseRequest", new()
        {
            ["CustomerId"] = customer, ["ProductId"] = product, ["StatusId"] = 3905, ["RequestedCages"] = 20,
            ["PickupDate"] = day, ["PlannedCages"] = 20, ["WorkDate"] = day, ["PlannedPickupDate"] = day,
            ["CagesPerEmployeePerDay"] = 20
        });
        var plan = await InsertAsync(sql, "WorkDayPlan", new()
        {
            ["CreatorAccountId"] = owner, ["ResponsibleAccountId"] = owner, ["RaftId"] = raft, ["JobTypeId"] = job,
            ["WorkDate"] = day, ["PurchaseRequestId"] = request, ["Approved"] = true, ["RespondedAt"] = DateTimeOffset.UtcNow
        });
        var assignment = await InsertAsync(sql, "WorkAssignments", new()
        {
            ["WorkDayPlanId"] = plan, ["ZoneId"] = zone, ["RaftId"] = raft, ["EmployeeId"] = employee,
            ["JobTypeId"] = job, ["WorkDate"] = day, ["PaymentType"] = "DailyWage"
        });
        var attendance = await InsertAsync(sql, "AttendanceRecords", new()
        {
            ["EmployeeId"] = employee, ["ZoneId"] = zone, ["WorkDate"] = day, ["RecordedByAccountId"] = owner, ["Result"] = "Present"
        });
        await InsertAsync(sql, "AttendanceJobPeriod", new()
        {
            ["AttendanceId"] = attendance, ["RaftId"] = raft, ["JobTypeId"] = job, ["StartsAt"] = DateTimeOffset.UtcNow
        });
        var invitation = await InsertAsync(sql, "PurchaseShiftInvitation", new()
        {
            ["PurchaseRequestId"] = request, ["EmployeeId"] = employee, ["Approved"] = true,
            ["RespondedAt"] = DateTimeOffset.UtcNow, ["ReservedWorkDate"] = day
        });
        await InsertAsync(sql, "LaborSettlements", new()
        {
            ["EmployeeId"] = employee, ["PeriodStart"] = new DateTime(2029, 12, 1), ["PeriodEnd"] = new DateTime(2029, 12, 31),
            ["PreparedByAccountId"] = owner, ["StatusId"] = 1502, ["TotalObligation"] = 1000m
        });
        await InsertAsync(sql, "AttendanceSettings", new()
        {
            ["EffectiveFrom"] = new DateTime(2029, 1, 1), ["RoundingMinutes"] = 15
        });

        // Reproduce the original deployed IDs, which later develop uses for PurchaseReturn instead.
        await using (var oldIds = new SqlCommand("""
            INSERT INTO [Statuses] ([Id],[Code],[DisplayColor],[DisplayName],[Group],[IsFinal],[IsInitial],[Meaning],[SortOrder])
            SELECT [Id]-2000,[Code],[DisplayColor],[DisplayName],N'PURCHASE_REQUEST',[IsFinal],[IsInitial],[Meaning],[SortOrder]
            FROM [Statuses] WHERE [Id] BETWEEN 3901 AND 3905;
            UPDATE [PurchaseRequest] SET [StatusId] = [StatusId]-2000 WHERE [StatusId] BETWEEN 3901 AND 3905;
            UPDATE [StatusTransitions] SET [FromStatusId] = [FromStatusId]-2000 WHERE [FromStatusId] BETWEEN 3901 AND 3905;
            UPDATE [StatusTransitions] SET [ToStatusId] = [ToStatusId]-2000 WHERE [ToStatusId] BETWEEN 3901 AND 3905;
            DELETE FROM [Statuses] WHERE [Id] BETWEEN 3901 AND 3905;
            """, sql)) await oldIds.ExecuteNonQueryAsync();

        await using var current = database.CreateContext();
        await current.Database.MigrateAsync();
        var saved = await current.PurchaseRequests.SingleAsync(r => r.Id == request);
        Assert.Equal(20, saved.RequestedCages);
        Assert.Equal(0, saved.RequestedCages);
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingStaff, saved.StatusId);
        Assert.Equal(plan, (await current.WorkAssignments.SingleAsync(a => a.Id == assignment)).WorkDayPlanId);
        Assert.Equal(1, await current.Set<AttendanceJobPeriod>().CountAsync(p => p.AttendanceId == attendance));
        Assert.True((await current.Set<PurchaseShiftInvitation>().SingleAsync(i => i.Id == invitation)).Approved);
        Assert.True((await current.Payrolls.SingleAsync()).IsLocked);
        Assert.Equal("LEGACY-PRODUCT", (await current.Products.SingleAsync(p => p.Id == product)).Code);
        Assert.Equal(15, (int)(await current.AttendanceSettingsVersions.SingleAsync()).RoundingUnit);
        var transition = await current.StatusTransitions.SingleAsync(t => t.Id == 30084);
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingStaff, transition.FromStatusId);
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, transition.ToStatusId);
    }

    // Required fixture fields are populated from the historical schema, so this test does not depend on today's model.
    private static async Task<Guid> InsertAsync(SqlConnection sql, string table, Dictionary<string, object> values)
    {
        var columns = new List<(string Name, string Type)>();
        await using (var query = new SqlCommand("SELECT c.name,t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID(@table) AND c.system_type_id<>189 AND c.is_identity=0 AND (c.is_nullable=0 AND c.default_object_id=0)", sql))
        {
            query.Parameters.AddWithValue("@table", "dbo." + table);
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add((reader.GetString(0), reader.GetString(1)));
        }
        var id = Guid.NewGuid(); values["Id"] = id;
        foreach (var (name, type) in columns.Where(c => !values.ContainsKey(c.Name)))
            values[name] = type switch
            {
                "uniqueidentifier" => Guid.NewGuid(), "int" or "smallint" or "bigint" => 0,
                "bit" => false, "decimal" or "numeric" or "money" => 1m,
                "date" or "datetime2" => new DateTime(2030, 1, 1), "datetimeoffset" => DateTimeOffset.UtcNow,
                _ => name.EndsWith("Urls") ? "[]" : Guid.NewGuid().ToString("N")[..8]
            };
        var entries = values.ToArray();
        await using var insert = new SqlCommand($"INSERT INTO [{table}] ({string.Join(',', entries.Select(e => $"[{e.Key}]"))}) VALUES ({string.Join(',', entries.Select((_, i) => $"@p{i}"))})", sql);
        for (var i = 0; i < entries.Length; i++) insert.Parameters.AddWithValue($"@p{i}", entries[i].Value);
        await insert.ExecuteNonQueryAsync();
        return id;
    }

#pragma warning disable EF1001 // Reproduce the already deployed branch's migration history in this regression test only.
    private sealed class LegacyMiniAppAssembly(ICurrentDbContext context, IDbContextOptions options,
        IMigrationsIdGenerator ids, IDiagnosticsLogger<DbLoggerCategory.Migrations> logger) : MigrationsAssembly(context, options, ids, logger)
    {
        private static readonly HashSet<string> Extra = ["20261030094130_AddEmployeeWorkforce", "20261030164158_AddLeaveRequestCancellation",
            "20261030191110_AddCagePurchaseRequests", "20261030203817_AddPurchaseStaffing", "20261106090714_AddWorkDayPlans"];
        public override IReadOnlyDictionary<string, TypeInfo> Migrations => base.Migrations
            .Where(m => string.CompareOrdinal(m.Key, "20261028151319") < 0 || Extra.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value);
    }
#pragma warning restore EF1001
}
