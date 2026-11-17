using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Decisions of 17/11/2026: a work assignment has a zone and may have no raft (nursery); the crew of a harvest lot is renamed HarvestRecordEmployees and the nursery and seeding records get their own
    /// employee tables; the job type Ươm is added to an existing catalogue. Existing assignments take the zone of their raft, so nothing that was counted changes.
    /// </summary>
    public partial class RecordEmployeesAndZoneWorkAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Work assignments: the zone of every existing row is the zone of its raft; then the column is required and the raft optional.
            migrationBuilder.AddColumn<Guid>(name: "ZoneId", table: "WorkAssignments", type: "uniqueidentifier", nullable: true);
            migrationBuilder.Sql("UPDATE w SET w.ZoneId = r.ZoneId FROM WorkAssignments w INNER JOIN Rafts r ON r.Id = w.RaftId");
            migrationBuilder.AlterColumn<Guid>(name: "ZoneId", table: "WorkAssignments", type: "uniqueidentifier", nullable: false, oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "RaftId", table: "WorkAssignments", type: "uniqueidentifier", nullable: true, oldClrType: typeof(Guid), oldType: "uniqueidentifier");
            migrationBuilder.CreateIndex(name: "IX_WorkAssignments_Date_ZoneId", table: "WorkAssignments", columns: new[] { "Date", "ZoneId" });
            migrationBuilder.CreateIndex(name: "IX_WorkAssignments_ZoneId", table: "WorkAssignments", column: "ZoneId");
            migrationBuilder.AddForeignKey(
                name: "FK_WorkAssignments_FarmingZones_ZoneId", table: "WorkAssignments", column: "ZoneId", principalTable: "FarmingZones", principalColumn: "Id", onDelete: ReferentialAction.Restrict);

            // The crew of a harvest lot keeps its rows: the table and its constraints are renamed.
            migrationBuilder.RenameTable(name: "HarvestLotCrew", newName: "HarvestRecordEmployees");
            migrationBuilder.Sql("EXEC sp_rename N'PK_HarvestLotCrew', N'PK_HarvestRecordEmployees', N'OBJECT'");
            migrationBuilder.Sql("EXEC sp_rename N'FK_HarvestLotCrew_Employees_EmployeeId', N'FK_HarvestRecordEmployees_Employees_EmployeeId', N'OBJECT'");
            migrationBuilder.Sql("EXEC sp_rename N'FK_HarvestLotCrew_HarvestLots_LotId', N'FK_HarvestRecordEmployees_HarvestLots_LotId', N'OBJECT'");
            migrationBuilder.RenameIndex(name: "IX_HarvestLotCrew_EmployeeId", table: "HarvestRecordEmployees", newName: "IX_HarvestRecordEmployees_EmployeeId");

            migrationBuilder.CreateTable(
                name: "NurseryRecordEmployees",
                columns: table => new
                {
                    NurseryBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NurseryRecordEmployees", x => new { x.NurseryBatchId, x.EmployeeId });
                    table.ForeignKey("FK_NurseryRecordEmployees_Employees_EmployeeId", x => x.EmployeeId, "Employees", "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey("FK_NurseryRecordEmployees_NurseryBatches_NurseryBatchId", x => x.NurseryBatchId, "NurseryBatches", "Id", onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.CreateIndex(name: "IX_NurseryRecordEmployees_EmployeeId", table: "NurseryRecordEmployees", column: "EmployeeId");

            migrationBuilder.CreateTable(
                name: "SeedingRecordEmployees",
                columns: table => new
                {
                    SeedingRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeedingRecordEmployees", x => new { x.SeedingRecordId, x.EmployeeId });
                    table.ForeignKey("FK_SeedingRecordEmployees_Employees_EmployeeId", x => x.EmployeeId, "Employees", "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey("FK_SeedingRecordEmployees_SeedingRecords_SeedingRecordId", x => x.SeedingRecordId, "SeedingRecords", "Id", onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.CreateIndex(name: "IX_SeedingRecordEmployees_EmployeeId", table: "SeedingRecordEmployees", column: "EmployeeId");

            // The job type Ươm joins Thu hoạch and Treo giống in a catalogue that already exists, at a placeholder rate the owner or the accountant changes. It is entered by the account that entered the other rates.
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM JobTypes WHERE Name = N'Ươm') AND EXISTS (SELECT 1 FROM PayRates)
BEGIN
    DECLARE @jobId uniqueidentifier = NEWID();
    DECLARE @by uniqueidentifier = (SELECT TOP 1 EnteredByAccountId FROM PayRates ORDER BY CreatedAt);
    INSERT INTO JobTypes (Id, Name, StatusId, CreatedAt, CreatedBy) VALUES (@jobId, N'Ươm', 101, SYSDATETIMEOFFSET(), @by);
    INSERT INTO PayRates (Id, JobTypeId, HourlyRate, EffectiveFrom, EnteredByAccountId, CreatedAt, CreatedBy)
    VALUES (NEWID(), @jobId, 30000, '2026-05-01', @by, SYSDATETIMEOFFSET(), @by);
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE r FROM PayRates r INNER JOIN JobTypes j ON j.Id = r.JobTypeId WHERE j.Name = N'Ươm' AND NOT EXISTS (SELECT 1 FROM WorkAssignments w WHERE w.JobTypeId = j.Id)");
            migrationBuilder.Sql("DELETE FROM JobTypes WHERE Name = N'Ươm' AND NOT EXISTS (SELECT 1 FROM WorkAssignments w WHERE w.JobTypeId = JobTypes.Id) AND NOT EXISTS (SELECT 1 FROM PayRates p WHERE p.JobTypeId = JobTypes.Id)");
            migrationBuilder.DropTable(name: "NurseryRecordEmployees");
            migrationBuilder.DropTable(name: "SeedingRecordEmployees");

            migrationBuilder.RenameIndex(name: "IX_HarvestRecordEmployees_EmployeeId", table: "HarvestRecordEmployees", newName: "IX_HarvestLotCrew_EmployeeId");
            migrationBuilder.Sql("EXEC sp_rename N'FK_HarvestRecordEmployees_HarvestLots_LotId', N'FK_HarvestLotCrew_HarvestLots_LotId', N'OBJECT'");
            migrationBuilder.Sql("EXEC sp_rename N'FK_HarvestRecordEmployees_Employees_EmployeeId', N'FK_HarvestLotCrew_Employees_EmployeeId', N'OBJECT'");
            migrationBuilder.Sql("EXEC sp_rename N'PK_HarvestRecordEmployees', N'PK_HarvestLotCrew', N'OBJECT'");
            migrationBuilder.RenameTable(name: "HarvestRecordEmployees", newName: "HarvestLotCrew");

            // Work at zone level has no raft to go back to: those assignments are removed.
            migrationBuilder.Sql("DELETE FROM WorkAssignments WHERE RaftId IS NULL");
            migrationBuilder.DropForeignKey(name: "FK_WorkAssignments_FarmingZones_ZoneId", table: "WorkAssignments");
            migrationBuilder.DropIndex(name: "IX_WorkAssignments_Date_ZoneId", table: "WorkAssignments");
            migrationBuilder.DropIndex(name: "IX_WorkAssignments_ZoneId", table: "WorkAssignments");
            migrationBuilder.DropColumn(name: "ZoneId", table: "WorkAssignments");
            migrationBuilder.AlterColumn<Guid>(
                name: "RaftId", table: "WorkAssignments", type: "uniqueidentifier", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);
        }
    }
}
