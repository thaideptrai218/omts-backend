using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShiftPayAndPayrollAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EmployeeSalarySetups_MonthlyAmount",
                table: "EmployeeSalarySetups");

            migrationBuilder.AddColumn<decimal>(
                name: "ManualDeduction",
                table: "PayrollLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SalaryType",
                table: "PayrollLines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ShiftPay",
                table: "PayrollLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Shifts",
                table: "PayrollLines",
                type: "decimal(18,1)",
                precision: 18,
                scale: 1,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "PayrollLineJobs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Hourly");

            migrationBuilder.AddColumn<decimal>(
                name: "ShiftRate",
                table: "PayrollLineJobs",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Shifts",
                table: "PayrollLineJobs",
                type: "decimal(18,1)",
                precision: 18,
                scale: 1,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "PayFieldShifts",
                table: "EmployeeSalarySetups",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ShiftMinutes",
                table: "AttendanceSettingsVersions",
                type: "int",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.AddColumn<int>(
                name: "ShiftToleranceMinutes",
                table: "AttendanceSettingsVersions",
                type: "int",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.CreateTable(
                name: "PayrollLineAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayrollLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollLineAdjustments", x => x.Id);
                    table.CheckConstraint("CK_PayrollLineAdjustments_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_PayrollLineAdjustments_PayrollLines_PayrollLineId",
                        column: x => x.PayrollLineId,
                        principalTable: "PayrollLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShiftRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
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
                    table.PrimaryKey("PK_ShiftRates", x => x.Id);
                    table.CheckConstraint("CK_ShiftRates_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_ShiftRates_Accounts_EnteredByAccountId",
                        column: x => x.EnteredByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShiftRates_JobTypes_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // The setups and payslips made before shifts existed keep their meaning: a fixed salary pays the shifts of field work (the default of the new setting), and every earlier payslip was by the hour.
            migrationBuilder.Sql("UPDATE [EmployeeSalarySetups] SET [PayFieldShifts] = 1 WHERE [Type] = 'FixedMonthly'");
            migrationBuilder.Sql("UPDATE [PayrollLines] SET [SalaryType] = 'Hourly'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmployeeSalarySetups_MonthlyAmount",
                table: "EmployeeSalarySetups",
                sql: "([Type] = 'FixedMonthly' AND [MonthlyAmount] > 0) OR ([Type] IN ('Hourly', 'PerShift') AND [MonthlyAmount] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmployeeSalarySetups_PayFieldShifts",
                table: "EmployeeSalarySetups",
                sql: "[Type] = 'FixedMonthly' OR [PayFieldShifts] = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AttendanceSettingsVersions_Shift",
                table: "AttendanceSettingsVersions",
                sql: "[ShiftMinutes] BETWEEN 30 AND 720 AND [ShiftToleranceMinutes] >= 0 AND [ShiftToleranceMinutes] * 2 <= [ShiftMinutes]");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLineAdjustments_PayrollLineId_Position",
                table: "PayrollLineAdjustments",
                columns: new[] { "PayrollLineId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_ShiftRates_EnteredByAccountId",
                table: "ShiftRates",
                column: "EnteredByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ShiftRates_JobTypeId_EffectiveFrom",
                table: "ShiftRates",
                columns: new[] { "JobTypeId", "EffectiveFrom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PayrollLineAdjustments");

            migrationBuilder.DropTable(
                name: "ShiftRates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmployeeSalarySetups_MonthlyAmount",
                table: "EmployeeSalarySetups");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmployeeSalarySetups_PayFieldShifts",
                table: "EmployeeSalarySetups");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AttendanceSettingsVersions_Shift",
                table: "AttendanceSettingsVersions");

            migrationBuilder.DropColumn(
                name: "ManualDeduction",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "SalaryType",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "ShiftPay",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "Shifts",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PayrollLineJobs");

            migrationBuilder.DropColumn(
                name: "ShiftRate",
                table: "PayrollLineJobs");

            migrationBuilder.DropColumn(
                name: "Shifts",
                table: "PayrollLineJobs");

            migrationBuilder.DropColumn(
                name: "PayFieldShifts",
                table: "EmployeeSalarySetups");

            migrationBuilder.DropColumn(
                name: "ShiftMinutes",
                table: "AttendanceSettingsVersions");

            migrationBuilder.DropColumn(
                name: "ShiftToleranceMinutes",
                table: "AttendanceSettingsVersions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmployeeSalarySetups_MonthlyAmount",
                table: "EmployeeSalarySetups",
                sql: "([Type] = 'FixedMonthly' AND [MonthlyAmount] > 0) OR ([Type] = 'Hourly' AND [MonthlyAmount] IS NULL)");
        }
    }
}
