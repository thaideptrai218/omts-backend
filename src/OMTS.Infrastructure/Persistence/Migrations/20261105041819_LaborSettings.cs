using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LaborSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutomaticPayroll",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PayPeriodStartDay",
                table: "Cooperatives",
                type: "int",
                nullable: false,
                // By hand: the start day is 1 when OMTS is set up (BR-LABOR-13), and the check below refuses 0. Regenerated: keep it.
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "AttendanceSettingsVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    BreakStart = table.Column<TimeOnly>(type: "time", nullable: true),
                    BreakEnd = table.Column<TimeOnly>(type: "time", nullable: true),
                    RoundingUnit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EnteredByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceSettingsVersions", x => x.Id);
                    table.CheckConstraint("CK_AttendanceSettingsVersions_Break", "([BreakStart] IS NULL AND [BreakEnd] IS NULL) OR ([BreakStart] IS NOT NULL AND [BreakEnd] IS NOT NULL AND [BreakEnd] > [BreakStart])");
                    table.ForeignKey(
                        name: "FK_AttendanceSettingsVersions_Accounts_EnteredByAccountId",
                        column: x => x.EnteredByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HourlyRate = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EnteredByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayRates", x => x.Id);
                    table.CheckConstraint("CK_PayRates_HourlyRate", "[HourlyRate] > 0");
                    table.ForeignKey(
                        name: "FK_PayRates_Accounts_EnteredByAccountId",
                        column: x => x.EnteredByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayRates_JobTypes_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payrolls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedBySystem = table.Column<bool>(type: "bit", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SendBackNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SentBackAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentBackByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinalizedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payrolls", x => x.Id);
                    table.CheckConstraint("CK_Payrolls_Period", "[PeriodEnd] >= [PeriodStart]");
                    table.ForeignKey(
                        name: "FK_Payrolls_Accounts_FinalizedByAccountId",
                        column: x => x.FinalizedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payrolls_Accounts_SentBackByAccountId",
                        column: x => x.SentBackByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payrolls_Accounts_SentByAccountId",
                        column: x => x.SentByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payrolls_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 3401, "DRAFT", null, "Draft", "PAYROLL", false, true, "Being prepared; its lines are calculated from the current records.", 1 },
                    { 3402, "AWAITING_FINALIZATION", null, "Awaiting finalization", "PAYROLL", false, false, "Sent to the Cooperative Owner; the records of the period are locked.", 2 },
                    { 3403, "SENT_BACK", null, "Sent back", "PAYROLL", false, false, "Returned with a note for correction; the records of the period can change again.", 3 },
                    { 3404, "FINALIZED", null, "Finalized", "PAYROLL", true, false, "The amounts are saved; never changed or reopened.", 4 }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cooperatives_PayPeriodStartDay",
                table: "Cooperatives",
                sql: "[PayPeriodStartDay] BETWEEN 1 AND 28");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSettingsVersions_EffectiveFrom",
                table: "AttendanceSettingsVersions",
                column: "EffectiveFrom",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSettingsVersions_EnteredByAccountId",
                table: "AttendanceSettingsVersions",
                column: "EnteredByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRates_EnteredByAccountId",
                table: "PayRates",
                column: "EnteredByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRates_JobTypeId_EffectiveFrom",
                table: "PayRates",
                columns: new[] { "JobTypeId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_FinalizedByAccountId",
                table: "Payrolls",
                column: "FinalizedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_PeriodEnd",
                table: "Payrolls",
                column: "PeriodEnd",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_PeriodStart",
                table: "Payrolls",
                column: "PeriodStart",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_SentBackByAccountId",
                table: "Payrolls",
                column: "SentBackByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_SentByAccountId",
                table: "Payrolls",
                column: "SentByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_StatusId",
                table: "Payrolls",
                column: "StatusId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceSettingsVersions");

            migrationBuilder.DropTable(
                name: "PayRates");

            migrationBuilder.DropTable(
                name: "Payrolls");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cooperatives_PayPeriodStartDay",
                table: "Cooperatives");

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3401);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3402);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3403);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3404);

            migrationBuilder.DropColumn(
                name: "AutomaticPayroll",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "PayPeriodStartDay",
                table: "Cooperatives");
        }
    }
}
