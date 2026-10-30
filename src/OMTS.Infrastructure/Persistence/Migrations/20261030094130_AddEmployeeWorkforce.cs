using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeWorkforce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkAssignments_EmployeeId",
                table: "WorkAssignments");

            migrationBuilder.AddColumn<Guid>(
                name: "JobTypeId",
                table: "WorkAssignments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckInMethod",
                table: "AttendanceRecords",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckOutMethod",
                table: "AttendanceRecords",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckedInAt",
                table: "AttendanceRecords",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckedOutAt",
                table: "AttendanceRecords",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                table: "AttendanceRecords",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AttendanceQr",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceQr", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceQr_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    BreakFrom = table.Column<TimeOnly>(type: "time", nullable: true),
                    BreakTo = table.Column<TimeOnly>(type: "time", nullable: true),
                    RoundingMinutes = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceSettings", x => x.Id);
                    table.CheckConstraint("CK_AttendanceSettings_Values", "([RoundingMinutes] IN (0, 15, 30, 60)) AND (([BreakFrom] IS NULL AND [BreakTo] IS NULL) OR ([BreakFrom] IS NOT NULL AND [BreakTo] IS NOT NULL AND [BreakTo] > [BreakFrom]))");
                });

            migrationBuilder.CreateTable(
                name: "EmployeeLeaveRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Approved = table.Column<bool>(type: "bit", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Response = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ClientOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeLeaveRequest", x => x.Id);
                    table.CheckConstraint("CK_EmployeeLeaveRequest_Dates", "[To] >= [From]");
                    table.ForeignKey(
                        name: "FK_EmployeeLeaveRequest_Accounts_ReviewedByAccountId",
                        column: x => x.ReviewedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeLeaveRequest_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobType",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobType", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobType_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceJobPeriod",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttendanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceJobPeriod", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceJobPeriod_AttendanceRecords_AttendanceId",
                        column: x => x.AttendanceId,
                        principalTable: "AttendanceRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceJobPeriod_JobType_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttendanceJobPeriod_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_EmployeeId_WorkDate",
                table: "WorkAssignments",
                columns: new[] { "EmployeeId", "WorkDate" },
                unique: true,
                filter: "[EmployeeId] IS NOT NULL AND [CancelledAt] IS NULL AND [JobTypeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_JobTypeId",
                table: "WorkAssignments",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_ZoneId",
                table: "AttendanceRecords",
                column: "ZoneId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AttendanceRecord_Times",
                table: "AttendanceRecords",
                sql: "[CheckedInAt] IS NULL OR [CheckedOutAt] IS NULL OR [CheckedOutAt] > [CheckedInAt]");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceJobPeriod_AttendanceId_StartsAt",
                table: "AttendanceJobPeriod",
                columns: new[] { "AttendanceId", "StartsAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceJobPeriod_JobTypeId",
                table: "AttendanceJobPeriod",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceJobPeriod_RaftId",
                table: "AttendanceJobPeriod",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceQr_EmployeeId",
                table: "AttendanceQr",
                column: "EmployeeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceQr_ExpiresAt",
                table: "AttendanceQr",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceQr_TokenHash",
                table: "AttendanceQr",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSettings_EffectiveFrom",
                table: "AttendanceSettings",
                column: "EffectiveFrom",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLeaveRequest_EmployeeId_ClientOperationId",
                table: "EmployeeLeaveRequest",
                columns: new[] { "EmployeeId", "ClientOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeLeaveRequest_ReviewedByAccountId",
                table: "EmployeeLeaveRequest",
                column: "ReviewedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_JobType_Name",
                table: "JobType",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobType_StatusId",
                table: "JobType",
                column: "StatusId");

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceRecords_FarmingZones_ZoneId",
                table: "AttendanceRecords",
                column: "ZoneId",
                principalTable: "FarmingZones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkAssignments_JobType_JobTypeId",
                table: "WorkAssignments",
                column: "JobTypeId",
                principalTable: "JobType",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendanceRecords_FarmingZones_ZoneId",
                table: "AttendanceRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkAssignments_JobType_JobTypeId",
                table: "WorkAssignments");

            migrationBuilder.DropTable(
                name: "AttendanceJobPeriod");

            migrationBuilder.DropTable(
                name: "AttendanceQr");

            migrationBuilder.DropTable(
                name: "AttendanceSettings");

            migrationBuilder.DropTable(
                name: "EmployeeLeaveRequest");

            migrationBuilder.DropTable(
                name: "JobType");

            migrationBuilder.DropIndex(
                name: "IX_WorkAssignments_EmployeeId_WorkDate",
                table: "WorkAssignments");

            migrationBuilder.DropIndex(
                name: "IX_WorkAssignments_JobTypeId",
                table: "WorkAssignments");

            migrationBuilder.DropIndex(
                name: "IX_AttendanceRecords_ZoneId",
                table: "AttendanceRecords");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AttendanceRecord_Times",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "JobTypeId",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "CheckInMethod",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "CheckOutMethod",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "CheckedInAt",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "CheckedOutAt",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "AttendanceRecords");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_EmployeeId",
                table: "WorkAssignments",
                column: "EmployeeId");
        }
    }
}
