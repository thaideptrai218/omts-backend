using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollSheetsPayslipsAndWagePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payrolls_PeriodEnd",
                table: "Payrolls");

            migrationBuilder.DropIndex(
                name: "IX_Payrolls_PeriodStart",
                table: "Payrolls");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "Payrolls",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByAccountId",
                table: "Payrolls",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Payrolls",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExpenseMonth",
                table: "Payrolls",
                type: "nvarchar(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Payrolls",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PeriodType",
                table: "Payrolls",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "Payrolls",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            // Headers older than the sheets (the demo seed made two) take a code and a period type; the BL counter continues after them.
            migrationBuilder.Sql(
                """
                UPDATE Payrolls SET PeriodType = CASE WHEN DAY(PeriodStart) = 1 AND PeriodEnd = EOMONTH(PeriodStart) THEN N'Monthly' ELSE N'Custom' END,
                                    Scope = N'All';
                UPDATE p SET p.Code = n.Code
                FROM Payrolls p
                JOIN (SELECT Id, N'BL' + FORMAT(ROW_NUMBER() OVER (ORDER BY PeriodStart, CreatedAt, Id), '000000') AS Code
                      FROM Payrolls) n ON n.Id = p.Id;
                DELETE FROM DocumentSequences WHERE [Key] = N'BL';
                INSERT INTO DocumentSequences ([Key], LastValue)
                SELECT N'BL', COUNT_BIG(*) FROM Payrolls HAVING COUNT_BIG(*) > 0;
                """);

            migrationBuilder.CreateTable(
                name: "PayrollLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayrollId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hours = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MainPay = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Bonus = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Deduction = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    HourlyRateNote = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollLines_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayrollLines_Payrolls_PayrollId",
                        column: x => x.PayrollId,
                        principalTable: "Payrolls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollSelectedEmployees",
                columns: table => new
                {
                    PayrollId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollSelectedEmployees", x => new { x.PayrollId, x.EmployeeId });
                    table.ForeignKey(
                        name: "FK_PayrollSelectedEmployees_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayrollSelectedEmployees_Payrolls_PayrollId",
                        column: x => x.PayrollId,
                        principalTable: "Payrolls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollLineJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayrollLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    HourlyRate = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Hours = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollLineJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollLineJobs_PayrollLines_PayrollLineId",
                        column: x => x.PayrollLineId,
                        principalTable: "PayrollLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WagePayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayrollLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CashVoucherId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RequestSequence = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WagePayments", x => x.Id);
                    table.CheckConstraint("CK_WagePayments_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_WagePayments_Line", "([Kind] = 'Wage' AND [PayrollLineId] IS NOT NULL) OR ([Kind] = 'Advance' AND [PayrollLineId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_WagePayments_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WagePayments_CashVouchers_CashVoucherId",
                        column: x => x.CashVoucherId,
                        principalTable: "CashVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WagePayments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WagePayments_PayrollLines_PayrollLineId",
                        column: x => x.PayrollLineId,
                        principalTable: "PayrollLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollAdvanceDeductions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdvanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayrollLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollAdvanceDeductions", x => x.Id);
                    table.CheckConstraint("CK_PayrollAdvanceDeductions_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_PayrollAdvanceDeductions_PayrollLines_PayrollLineId",
                        column: x => x.PayrollLineId,
                        principalTable: "PayrollLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayrollAdvanceDeductions_WagePayments_AdvanceId",
                        column: x => x.AdvanceId,
                        principalTable: "WagePayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 234, "Cooperative Owner", 3401, "The period has ended, no attendance of it is incomplete, no hours lack a rate and no request for it awaits review.", false, 3404, "Action", "LABOR-13" });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 3405, "CREATING", null, "Creating", "PAYROLL", false, false, "The sheet is being made.", 5 },
                    { 3406, "CANCELLED", null, "Cancelled", "PAYROLL", true, false, "Dropped before it was finalized; it no longer counts.", 6 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 233, "Cooperative Owner", 3405, "The lines of the period are calculated.", false, 3401, "Action", "LABOR-12" },
                    { 235, "Cooperative Owner", 3401, "A payroll sheet dropped before it is finalized.", false, 3406, "Action", "LABOR-12" },
                    { 236, "Cooperative Owner", 3405, "A sheet still being made is dropped.", false, 3406, "Action", "LABOR-12" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_CancelledByAccountId",
                table: "Payrolls",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_Code",
                table: "Payrolls",
                column: "Code",
                unique: true,
                filter: "[Code] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_PeriodEnd",
                table: "Payrolls",
                column: "PeriodEnd");

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_PeriodStart_PeriodEnd",
                table: "Payrolls",
                columns: new[] { "PeriodStart", "PeriodEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdvanceDeductions_AdvanceId",
                table: "PayrollAdvanceDeductions",
                column: "AdvanceId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdvanceDeductions_PayrollLineId",
                table: "PayrollAdvanceDeductions",
                column: "PayrollLineId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLineJobs_PayrollLineId",
                table: "PayrollLineJobs",
                column: "PayrollLineId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLines_Code",
                table: "PayrollLines",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLines_EmployeeId",
                table: "PayrollLines",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollLines_PayrollId_EmployeeId",
                table: "PayrollLines",
                columns: new[] { "PayrollId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollSelectedEmployees_EmployeeId",
                table: "PayrollSelectedEmployees",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_WagePayments_CashVoucherId",
                table: "WagePayments",
                column: "CashVoucherId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WagePayments_EmployeeId_Kind",
                table: "WagePayments",
                columns: new[] { "EmployeeId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_WagePayments_PaidAt",
                table: "WagePayments",
                column: "PaidAt");

            migrationBuilder.CreateIndex(
                name: "IX_WagePayments_PayrollLineId",
                table: "WagePayments",
                column: "PayrollLineId");

            migrationBuilder.CreateIndex(
                name: "IX_WagePayments_RecordedByAccountId",
                table: "WagePayments",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WagePayments_RequestKey_RequestSequence",
                table: "WagePayments",
                columns: new[] { "RequestKey", "RequestSequence" },
                unique: true,
                filter: "[RequestKey] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Payrolls_Accounts_CancelledByAccountId",
                table: "Payrolls",
                column: "CancelledByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM DocumentSequences WHERE [Key] IN (N'BL', N'PL');");

            migrationBuilder.DropForeignKey(
                name: "FK_Payrolls_Accounts_CancelledByAccountId",
                table: "Payrolls");

            migrationBuilder.DropTable(
                name: "PayrollAdvanceDeductions");

            migrationBuilder.DropTable(
                name: "PayrollLineJobs");

            migrationBuilder.DropTable(
                name: "PayrollSelectedEmployees");

            migrationBuilder.DropTable(
                name: "WagePayments");

            migrationBuilder.DropTable(
                name: "PayrollLines");

            migrationBuilder.DropIndex(
                name: "IX_Payrolls_CancelledByAccountId",
                table: "Payrolls");

            migrationBuilder.DropIndex(
                name: "IX_Payrolls_Code",
                table: "Payrolls");

            migrationBuilder.DropIndex(
                name: "IX_Payrolls_PeriodEnd",
                table: "Payrolls");

            migrationBuilder.DropIndex(
                name: "IX_Payrolls_PeriodStart_PeriodEnd",
                table: "Payrolls");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 233);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 234);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 235);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 236);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3405);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3406);

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "CancelledByAccountId",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "ExpenseMonth",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "PeriodType",
                table: "Payrolls");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "Payrolls");

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
        }
    }
}
