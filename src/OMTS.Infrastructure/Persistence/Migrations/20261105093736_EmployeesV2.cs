using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeesV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceRecords");

            migrationBuilder.DropTable(
                name: "WorkAssignments");

            migrationBuilder.DropTable(
                name: "Contractors");

            migrationBuilder.DropTable(
                name: "SeasonalWorkers");

            migrationBuilder.DropIndex(
                name: "IX_Employees_FullName_PhoneNumber",
                table: "Employees");

            migrationBuilder.RenameColumn(
                name: "EmploymentEndDate",
                table: "Employees",
                newName: "LastWorkingDay");

            migrationBuilder.CreateTable(
                name: "EmployeeTypePeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeTypePeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeTypePeriods_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobTypes_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeJobTypes",
                columns: table => new
                {
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeJobTypes", x => new { x.EmployeeId, x.JobTypeId });
                    table.ForeignKey(
                        name: "FK_EmployeeJobTypes_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeJobTypes_JobTypes_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner; Zone Manager; System", "The last working day has passed: at once for an earlier day, by the system on the next day for today (BR-LABOR-01).", "LABOR-01" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 7,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner; Zone Manager", "No Employed employee has the phone number; needs no new approval (BR-LABOR-01).", "LABOR-01" });

            // Hand-written, for the new index and the type every employee now has: of the Employed employees sharing a
            // number the first keeps it, and existing profiles (permanent employees until now) are Staff from their creation.
            migrationBuilder.Sql("""
                WITH Numbered AS (
                    SELECT PhoneNumber, ROW_NUMBER() OVER (PARTITION BY PhoneNumber ORDER BY CreatedAt, Id) AS Position
                    FROM Employees WHERE PhoneNumber IS NOT NULL AND StatusId = 401)
                UPDATE Numbered SET PhoneNumber = NULL WHERE Position > 1;
                INSERT INTO EmployeeTypePeriods (Id, EmployeeId, [Type], EffectiveFrom, CreatedAt)
                SELECT NEWID(), Id, N'Staff', CAST(CreatedAt AS date), CreatedAt FROM Employees;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_Employees_PhoneNumber_Employed",
                table: "Employees",
                column: "PhoneNumber",
                unique: true,
                filter: "[PhoneNumber] IS NOT NULL AND [StatusId] = 401");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeJobTypes_JobTypeId",
                table: "EmployeeJobTypes",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeTypePeriods_EmployeeId_EffectiveFrom",
                table: "EmployeeTypePeriods",
                columns: new[] { "EmployeeId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTypes_Name",
                table: "JobTypes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTypes_StatusId",
                table: "JobTypes",
                column: "StatusId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEvents_Employees_ActorEmployeeId",
                table: "AuditEvents",
                column: "ActorEmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditEvents_Employees_ActorEmployeeId",
                table: "AuditEvents");

            migrationBuilder.DropTable(
                name: "EmployeeJobTypes");

            migrationBuilder.DropTable(
                name: "EmployeeTypePeriods");

            migrationBuilder.DropTable(
                name: "JobTypes");

            migrationBuilder.DropIndex(
                name: "UX_Employees_PhoneNumber_Employed",
                table: "Employees");

            migrationBuilder.RenameColumn(
                name: "LastWorkingDay",
                table: "Employees",
                newName: "EmploymentEndDate");

            migrationBuilder.CreateTable(
                name: "Contractors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OtherContacts = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contractors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contractors_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeasonalWorkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CitizenId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonalWorkers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeasonalWorkers_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeasonalWorkers_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Result = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceRecords", x => x.Id);
                    table.CheckConstraint("CK_AttendanceRecord_OnePerson", "(CASE WHEN [EmployeeId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [WorkerId] IS NULL THEN 0 ELSE 1 END) = 1");
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_Contractors_ContractorId",
                        column: x => x.ContractorId,
                        principalTable: "Contractors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceRecords_SeasonalWorkers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "SeasonalWorkers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcceptanceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AcceptanceEvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AcceptancePassed = table.Column<bool>(type: "bit", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContractAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpectedCompletionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    Task = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAssignments", x => x.Id);
                    table.CheckConstraint("CK_WorkAssignment_PaymentType", "([PaymentType] = N'DailyWage' AND (CASE WHEN [EmployeeId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [WorkerId] IS NULL THEN 0 ELSE 1 END) = 1) OR ([PaymentType] = N'TaskContract' AND [ContractorId] IS NOT NULL AND [ContractAmount] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkAssignments_Accounts_CancelledByAccountId",
                        column: x => x.CancelledByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_Contractors_ContractorId",
                        column: x => x.ContractorId,
                        principalTable: "Contractors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAssignments_SeasonalWorkers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "SeasonalWorkers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner", "The employee leaves.", "UC10" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 7,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner", "The employee returns to work.", "UC10" });

            // Hand-written: the old index allows one profile per name and number, while a Left employee and a new one may share both now.
            migrationBuilder.Sql("""
                WITH Numbered AS (
                    SELECT PhoneNumber, ROW_NUMBER() OVER (PARTITION BY FullName, PhoneNumber ORDER BY CreatedAt, Id) AS Position
                    FROM Employees WHERE PhoneNumber IS NOT NULL)
                UPDATE Numbered SET PhoneNumber = NULL WHERE Position > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_FullName_PhoneNumber",
                table: "Employees",
                columns: new[] { "FullName", "PhoneNumber" },
                unique: true,
                filter: "[PhoneNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_ContractorId",
                table: "AttendanceRecords",
                column: "ContractorId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_EmployeeId_WorkDate",
                table: "AttendanceRecords",
                columns: new[] { "EmployeeId", "WorkDate" },
                unique: true,
                filter: "[EmployeeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_RecordedByAccountId",
                table: "AttendanceRecords",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_WorkerId_WorkDate",
                table: "AttendanceRecords",
                columns: new[] { "WorkerId", "WorkDate" },
                unique: true,
                filter: "[WorkerId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Contractors_StatusId",
                table: "Contractors",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_CitizenId",
                table: "SeasonalWorkers",
                column: "CitizenId",
                unique: true,
                filter: "[CitizenId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_EmployeeId",
                table: "SeasonalWorkers",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_FullName_PhoneNumber",
                table: "SeasonalWorkers",
                columns: new[] { "FullName", "PhoneNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_StatusId",
                table: "SeasonalWorkers",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_CancelledByAccountId",
                table: "WorkAssignments",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_ContractorId",
                table: "WorkAssignments",
                column: "ContractorId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_CycleId",
                table: "WorkAssignments",
                column: "CycleId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_EmployeeId",
                table: "WorkAssignments",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_RaftId",
                table: "WorkAssignments",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_WorkerId",
                table: "WorkAssignments",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_ZoneId_WorkDate",
                table: "WorkAssignments",
                columns: new[] { "ZoneId", "WorkDate" });
        }
    }
}
