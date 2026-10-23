using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkforceAndPayroll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No API wrote attendance, pay rates, payslips, pay lines, payments or task contracts before OMTS-212, and their
            // old shape (no zone, no payroll, rates per subject type) cannot be converted: refuse such a database clearly
            // instead of failing on a foreign key half-way. Workers, teams and daily-wage assignments are kept.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM [AttendanceRecords]) OR EXISTS (SELECT 1 FROM [PayRuleVersions]) " +
                "OR EXISTS (SELECT 1 FROM [LaborSettlements]) OR EXISTS (SELECT 1 FROM [PayLines]) OR EXISTS (SELECT 1 FROM [LaborPayments]) " +
                "OR EXISTS (SELECT 1 FROM [WorkAssignments] WHERE [PaymentType] <> N'DailyWage' OR [ContractorId] IS NOT NULL) " +
                "THROW 50212, N'AddWorkforceAndPayroll: attendance, pay rates, payslips, payments and task contracts of the old labor model must be removed first.', 1;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkAssignment_PaymentType",
                table: "WorkAssignments");

            migrationBuilder.DropIndex(
                name: "IX_SeasonalWorkers_FullName_PhoneNumber",
                table: "SeasonalWorkers");

            migrationBuilder.DropIndex(
                name: "IX_PayRuleVersions_EmployeeId",
                table: "PayRuleVersions");

            migrationBuilder.DropIndex(
                name: "IX_PayRuleVersions_SubjectType_EmployeeId_ValidFrom",
                table: "PayRuleVersions");

            migrationBuilder.DropColumn(
                name: "AcceptancePassed",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "DailyWageRate",
                table: "PayRuleVersions");

            migrationBuilder.DropColumn(
                name: "MonthlySalary",
                table: "PayRuleVersions");

            // The planned piece quantity is not the old contract amount, and the salary type is not the old subject type.
            migrationBuilder.DropColumn(
                name: "ContractAmount",
                table: "WorkAssignments");

            migrationBuilder.AddColumn<decimal>(
                name: "PlannedQuantity",
                table: "WorkAssignments",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // Acceptance evidence and date are the completion evidence and date of LAB-05.
            migrationBuilder.RenameColumn(
                name: "AcceptanceEvidenceUrls",
                table: "WorkAssignments",
                newName: "CompletionEvidenceUrls");

            migrationBuilder.RenameColumn(
                name: "AcceptanceDate",
                table: "WorkAssignments",
                newName: "CompletionDate");

            migrationBuilder.DropColumn(
                name: "SubjectType",
                table: "PayRuleVersions");

            migrationBuilder.AddColumn<string>(
                name: "SalaryType",
                table: "PayRuleVersions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "CompletedQuantity",
                table: "WorkAssignments",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletionConfirmedAt",
                table: "WorkAssignments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompletionConfirmedByAccountId",
                table: "WorkAssignments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionNote",
                table: "WorkAssignments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "WorkAssignments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PieceRate",
                table: "WorkAssignments",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PieceUnit",
                table: "WorkAssignments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContractorId",
                table: "SeasonalWorkers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "PayRuleVersions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Components",
                table: "PayRuleVersions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StandardWorkingDays",
                table: "PayRuleVersions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkerId",
                table: "PayRuleVersions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "PayLines",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsManual",
                table: "PayLines",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "Quantity",
                table: "PayLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitRate",
                table: "PayLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MissingRateDates",
                table: "LaborSettlements",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "PayrollId",
                table: "LaborSettlements",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "WorkerId",
                table: "LaborSettlements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SettlementId",
                table: "LaborPayments",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<string>(
                name: "ReferenceCode",
                table: "LaborPayments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeductedInSettlementId",
                table: "LaborPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EmployeeId",
                table: "LaborPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAdvance",
                table: "LaborPayments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "LaborPayments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkerId",
                table: "LaborPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaderWorkerId",
                table: "Contractors",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Hours",
                table: "AttendanceRecords",
                type: "decimal(4,1)",
                precision: 4,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                table: "AttendanceRecords",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Payrolls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    PreparedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReturnedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReturnedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                    table.ForeignKey(
                        name: "FK_Payrolls_Accounts_FinalizedByAccountId",
                        column: x => x.FinalizedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payrolls_Accounts_PreparedByAccountId",
                        column: x => x.PreparedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payrolls_Accounts_ReturnedByAccountId",
                        column: x => x.ReturnedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_CompletionConfirmedByAccountId",
                table: "WorkAssignments",
                column: "CompletionConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_CompletionDate",
                table: "WorkAssignments",
                column: "CompletionDate");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkAssignment_PaymentType",
                table: "WorkAssignments",
                sql: "(CASE WHEN [EmployeeId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [WorkerId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [ContractorId] IS NULL THEN 0 ELSE 1 END) = 1 AND ([PaymentType] = N'DailyWage' OR ([PaymentType] = N'Piecework' AND [PieceUnit] IS NOT NULL AND [PlannedQuantity] IS NOT NULL AND [PieceRate] IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_ContractorId",
                table: "SeasonalWorkers",
                column: "ContractorId");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_FullName_PhoneNumber",
                table: "SeasonalWorkers",
                columns: new[] { "FullName", "PhoneNumber" },
                unique: true,
                filter: "[PhoneNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_EmployeeId_ValidFrom",
                table: "PayRuleVersions",
                columns: new[] { "EmployeeId", "ValidFrom" },
                unique: true,
                filter: "[EmployeeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_WorkerId_ValidFrom",
                table: "PayRuleVersions",
                columns: new[] { "WorkerId", "ValidFrom" },
                unique: true,
                filter: "[WorkerId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PayRuleVersion_Amount_Thousands",
                table: "PayRuleVersions",
                sql: "[Amount] % 1000 = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PayRuleVersion_OnePerson",
                table: "PayRuleVersions",
                sql: "(CASE WHEN [EmployeeId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [WorkerId] IS NULL THEN 0 ELSE 1 END) = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PayLine_Amount_NotNegative",
                table: "PayLines",
                sql: "[Amount] >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_PayrollId_ContractorId",
                table: "LaborSettlements",
                columns: new[] { "PayrollId", "ContractorId" },
                unique: true,
                filter: "[ContractorId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_PayrollId_EmployeeId",
                table: "LaborSettlements",
                columns: new[] { "PayrollId", "EmployeeId" },
                unique: true,
                filter: "[EmployeeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_PayrollId_WorkerId",
                table: "LaborSettlements",
                columns: new[] { "PayrollId", "WorkerId" },
                unique: true,
                filter: "[WorkerId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_WorkerId",
                table: "LaborSettlements",
                column: "WorkerId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LaborSettlement_OnePayee",
                table: "LaborSettlements",
                sql: "(CASE WHEN [EmployeeId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [WorkerId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [ContractorId] IS NULL THEN 0 ELSE 1 END) = 1");

            migrationBuilder.CreateIndex(
                name: "IX_LaborPayments_DeductedInSettlementId",
                table: "LaborPayments",
                column: "DeductedInSettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborPayments_EmployeeId",
                table: "LaborPayments",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborPayments_WorkerId",
                table: "LaborPayments",
                column: "WorkerId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LaborPayment_Target",
                table: "LaborPayments",
                sql: "([IsAdvance] = 0 AND [SettlementId] IS NOT NULL) OR ([IsAdvance] = 1 AND [SettlementId] IS NULL AND (CASE WHEN [EmployeeId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [WorkerId] IS NULL THEN 0 ELSE 1 END) = 1)");

            migrationBuilder.CreateIndex(
                name: "IX_Contractors_LeaderWorkerId",
                table: "Contractors",
                column: "LeaderWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_Contractors_Name",
                table: "Contractors",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceRecords_ZoneId_WorkDate",
                table: "AttendanceRecords",
                columns: new[] { "ZoneId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_FinalizedByAccountId",
                table: "Payrolls",
                column: "FinalizedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_Period",
                table: "Payrolls",
                column: "Period",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_PreparedByAccountId",
                table: "Payrolls",
                column: "PreparedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Payrolls_ReturnedByAccountId",
                table: "Payrolls",
                column: "ReturnedByAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceRecords_FarmingZones_ZoneId",
                table: "AttendanceRecords",
                column: "ZoneId",
                principalTable: "FarmingZones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Contractors_SeasonalWorkers_LeaderWorkerId",
                table: "Contractors",
                column: "LeaderWorkerId",
                principalTable: "SeasonalWorkers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaborPayments_Employees_EmployeeId",
                table: "LaborPayments",
                column: "EmployeeId",
                principalTable: "Employees",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaborPayments_LaborSettlements_DeductedInSettlementId",
                table: "LaborPayments",
                column: "DeductedInSettlementId",
                principalTable: "LaborSettlements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaborPayments_SeasonalWorkers_WorkerId",
                table: "LaborPayments",
                column: "WorkerId",
                principalTable: "SeasonalWorkers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaborSettlements_Payrolls_PayrollId",
                table: "LaborSettlements",
                column: "PayrollId",
                principalTable: "Payrolls",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaborSettlements_SeasonalWorkers_WorkerId",
                table: "LaborSettlements",
                column: "WorkerId",
                principalTable: "SeasonalWorkers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PayRuleVersions_SeasonalWorkers_WorkerId",
                table: "PayRuleVersions",
                column: "WorkerId",
                principalTable: "SeasonalWorkers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeasonalWorkers_Contractors_ContractorId",
                table: "SeasonalWorkers",
                column: "ContractorId",
                principalTable: "Contractors",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkAssignments_Accounts_CompletionConfirmedByAccountId",
                table: "WorkAssignments",
                column: "CompletionConfirmedByAccountId",
                principalTable: "Accounts",
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
                name: "FK_Contractors_SeasonalWorkers_LeaderWorkerId",
                table: "Contractors");

            migrationBuilder.DropForeignKey(
                name: "FK_LaborPayments_Employees_EmployeeId",
                table: "LaborPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_LaborPayments_LaborSettlements_DeductedInSettlementId",
                table: "LaborPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_LaborPayments_SeasonalWorkers_WorkerId",
                table: "LaborPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_LaborSettlements_Payrolls_PayrollId",
                table: "LaborSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_LaborSettlements_SeasonalWorkers_WorkerId",
                table: "LaborSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_PayRuleVersions_SeasonalWorkers_WorkerId",
                table: "PayRuleVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_SeasonalWorkers_Contractors_ContractorId",
                table: "SeasonalWorkers");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkAssignments_Accounts_CompletionConfirmedByAccountId",
                table: "WorkAssignments");

            migrationBuilder.DropTable(
                name: "Payrolls");

            migrationBuilder.DropIndex(
                name: "IX_WorkAssignments_CompletionConfirmedByAccountId",
                table: "WorkAssignments");

            migrationBuilder.DropIndex(
                name: "IX_WorkAssignments_CompletionDate",
                table: "WorkAssignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkAssignment_PaymentType",
                table: "WorkAssignments");

            migrationBuilder.DropIndex(
                name: "IX_SeasonalWorkers_ContractorId",
                table: "SeasonalWorkers");

            migrationBuilder.DropIndex(
                name: "IX_SeasonalWorkers_FullName_PhoneNumber",
                table: "SeasonalWorkers");

            migrationBuilder.DropIndex(
                name: "IX_PayRuleVersions_EmployeeId_ValidFrom",
                table: "PayRuleVersions");

            migrationBuilder.DropIndex(
                name: "IX_PayRuleVersions_WorkerId_ValidFrom",
                table: "PayRuleVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PayRuleVersion_Amount_Thousands",
                table: "PayRuleVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PayRuleVersion_OnePerson",
                table: "PayRuleVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PayLine_Amount_NotNegative",
                table: "PayLines");

            migrationBuilder.DropIndex(
                name: "IX_LaborSettlements_PayrollId_ContractorId",
                table: "LaborSettlements");

            migrationBuilder.DropIndex(
                name: "IX_LaborSettlements_PayrollId_EmployeeId",
                table: "LaborSettlements");

            migrationBuilder.DropIndex(
                name: "IX_LaborSettlements_PayrollId_WorkerId",
                table: "LaborSettlements");

            migrationBuilder.DropIndex(
                name: "IX_LaborSettlements_WorkerId",
                table: "LaborSettlements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LaborSettlement_OnePayee",
                table: "LaborSettlements");

            migrationBuilder.DropIndex(
                name: "IX_LaborPayments_DeductedInSettlementId",
                table: "LaborPayments");

            migrationBuilder.DropIndex(
                name: "IX_LaborPayments_EmployeeId",
                table: "LaborPayments");

            migrationBuilder.DropIndex(
                name: "IX_LaborPayments_WorkerId",
                table: "LaborPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LaborPayment_Target",
                table: "LaborPayments");

            migrationBuilder.DropIndex(
                name: "IX_Contractors_LeaderWorkerId",
                table: "Contractors");

            migrationBuilder.DropIndex(
                name: "IX_Contractors_Name",
                table: "Contractors");

            migrationBuilder.DropIndex(
                name: "IX_AttendanceRecords_ZoneId_WorkDate",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "CompletedQuantity",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "CompletionConfirmedAt",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "CompletionConfirmedByAccountId",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "CompletionNote",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "PieceRate",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "PieceUnit",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "ContractorId",
                table: "SeasonalWorkers");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "PayRuleVersions");

            migrationBuilder.DropColumn(
                name: "Components",
                table: "PayRuleVersions");

            migrationBuilder.DropColumn(
                name: "StandardWorkingDays",
                table: "PayRuleVersions");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "PayRuleVersions");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "PayLines");

            migrationBuilder.DropColumn(
                name: "IsManual",
                table: "PayLines");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "PayLines");

            migrationBuilder.DropColumn(
                name: "UnitRate",
                table: "PayLines");

            migrationBuilder.DropColumn(
                name: "MissingRateDates",
                table: "LaborSettlements");

            migrationBuilder.DropColumn(
                name: "PayrollId",
                table: "LaborSettlements");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "LaborSettlements");

            migrationBuilder.DropColumn(
                name: "DeductedInSettlementId",
                table: "LaborPayments");

            migrationBuilder.DropColumn(
                name: "EmployeeId",
                table: "LaborPayments");

            migrationBuilder.DropColumn(
                name: "IsAdvance",
                table: "LaborPayments");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "LaborPayments");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "LaborPayments");

            migrationBuilder.DropColumn(
                name: "LeaderWorkerId",
                table: "Contractors");

            migrationBuilder.DropColumn(
                name: "Hours",
                table: "AttendanceRecords");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "AttendanceRecords");

            // Piece quantity and salary type are new concepts, not renames of the contract amount and subject type.
            migrationBuilder.DropColumn(
                name: "PlannedQuantity",
                table: "WorkAssignments");

            migrationBuilder.AddColumn<decimal>(
                name: "ContractAmount",
                table: "WorkAssignments",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.RenameColumn(
                name: "CompletionEvidenceUrls",
                table: "WorkAssignments",
                newName: "AcceptanceEvidenceUrls");

            migrationBuilder.RenameColumn(
                name: "CompletionDate",
                table: "WorkAssignments",
                newName: "AcceptanceDate");

            migrationBuilder.DropColumn(
                name: "SalaryType",
                table: "PayRuleVersions");

            migrationBuilder.AddColumn<string>(
                name: "SubjectType",
                table: "PayRuleVersions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");


            migrationBuilder.AddColumn<bool>(
                name: "AcceptancePassed",
                table: "WorkAssignments",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DailyWageRate",
                table: "PayRuleVersions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MonthlySalary",
                table: "PayRuleVersions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SettlementId",
                table: "LaborPayments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ReferenceCode",
                table: "LaborPayments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkAssignment_PaymentType",
                table: "WorkAssignments",
                sql: "([PaymentType] = N'DailyWage' AND (CASE WHEN [EmployeeId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [WorkerId] IS NULL THEN 0 ELSE 1 END) = 1) OR ([PaymentType] = N'TaskContract' AND [ContractorId] IS NOT NULL AND [ContractAmount] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_SeasonalWorkers_FullName_PhoneNumber",
                table: "SeasonalWorkers",
                columns: new[] { "FullName", "PhoneNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_EmployeeId",
                table: "PayRuleVersions",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_SubjectType_EmployeeId_ValidFrom",
                table: "PayRuleVersions",
                columns: new[] { "SubjectType", "EmployeeId", "ValidFrom" });
        }
    }
}
