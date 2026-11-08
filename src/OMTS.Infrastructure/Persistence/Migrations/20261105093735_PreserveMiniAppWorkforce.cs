using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OMTS.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OmtsDbContext))]
[Migration("20261105093735_PreserveMiniAppWorkforce")]
// Inherit only the preceding migration's target model so EF can type seed operations on rollback.
public sealed class PreserveMiniAppWorkforce : AuditSearch
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var name in new[] { "AttendanceRecords", "WorkAssignments", "AttendanceJobPeriod", "WorkDayPlan" })
            migrationBuilder.Sql($"IF OBJECT_ID(N'[dbo].[{name}]', N'U') IS NOT NULL AND OBJECT_ID(N'[dbo].[MiniAppLegacy{name}]', N'U') IS NULL " +
                $"EXEC(N'SELECT * INTO [dbo].[MiniAppLegacy{name}] FROM [dbo].[{name}]');");

        // The incoming EmployeesV2 retires the two parent tables; preserve and remove only our extra FK dependants first.
        migrationBuilder.Sql("""
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME([parent_object_id])) + N'.' + QUOTENAME(OBJECT_NAME([parent_object_id])) + N' DROP CONSTRAINT ' + QUOTENAME([name]) + N';'
            FROM sys.foreign_keys WHERE [parent_object_id] IN (OBJECT_ID(N'dbo.AttendanceJobPeriod'), OBJECT_ID(N'dbo.WorkDayPlan'))
               OR [referenced_object_id] = OBJECT_ID(N'dbo.WorkDayPlan');
            EXEC sp_executesql @sql;
            IF OBJECT_ID(N'dbo.AttendanceJobPeriod', N'U') IS NOT NULL DROP TABLE [AttendanceJobPeriod];
            IF OBJECT_ID(N'dbo.WorkDayPlan', N'U') IS NOT NULL DROP TABLE [WorkDayPlan];
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
