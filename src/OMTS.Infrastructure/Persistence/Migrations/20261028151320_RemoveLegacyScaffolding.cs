using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyScaffolding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #20 used these rows to lock workforce edits. Never silently drop an existing lock.
            // Payroll v2 has no write endpoint yet; a database with legacy locks needs a separate
            // data migration before this scaffolding cleanup can be applied.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [LaborSettlements] WHERE [StatusId] <> 1501)
                    THROW 51001, 'Legacy payroll periods are locked. Preserve their locks before removing the legacy payroll schema.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_LaneSegments_HarvestEvents_HarvestEventId",
                table: "LaneSegments");

            migrationBuilder.DropForeignKey(
                name: "FK_LaneSegments_SalesOrders_OrderId",
                table: "LaneSegments");

            migrationBuilder.DropForeignKey(
                name: "FK_LaneSegments_TraceabilityLots_LotId",
                table: "LaneSegments");

            migrationBuilder.DropForeignKey(
                name: "FK_PhysicalReturns_SaleTransactions_SaleId",
                table: "PhysicalReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_TraceabilityLots_SalesOrders_InitialOrderId",
                table: "TraceabilityLots");

            migrationBuilder.DropTable(
                name: "CustomerRefunds");

            migrationBuilder.DropTable(
                name: "HarvestEvents");

            migrationBuilder.DropTable(
                name: "LaborPayments");

            migrationBuilder.DropTable(
                name: "MessageDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "PayLines");

            migrationBuilder.DropTable(
                name: "PortHandovers");

            migrationBuilder.DropTable(
                name: "ReceiptAllocations");

            migrationBuilder.DropTable(
                name: "TraceCorrections");

            migrationBuilder.DropTable(
                name: "CustomerCredits");

            migrationBuilder.DropTable(
                name: "LaborSettlements");

            migrationBuilder.DropTable(
                name: "PayRuleVersions");

            migrationBuilder.DropTable(
                name: "TracePublicationVersions");

            migrationBuilder.DropTable(
                name: "CustomerReceipts");

            migrationBuilder.DropTable(
                name: "SaleAdjustments");

            migrationBuilder.DropTable(
                name: "QualityClaims");

            migrationBuilder.DropTable(
                name: "SaleTransactions");

            migrationBuilder.DropTable(
                name: "PortWeighings");

            migrationBuilder.DropTable(
                name: "SalesOrders");

            migrationBuilder.DropTable(
                name: "PhysicalReturns");

            migrationBuilder.DropTable(
                name: "TraceabilityLots");

            migrationBuilder.DropIndex(
                name: "IX_LaneSegments_HarvestEventId",
                table: "LaneSegments");

            migrationBuilder.DropIndex(
                name: "IX_LaneSegments_LotId",
                table: "LaneSegments");

            migrationBuilder.DropIndex(
                name: "IX_LaneSegments_OrderId",
                table: "LaneSegments");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 13);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 14);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 15);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 16);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 17);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 18);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 19);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 20);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 21);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 22);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 23);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 24);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 25);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 26);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 27);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 28);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 29);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 31);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 32);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 33);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 34);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 35);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 36);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 37);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 38);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 39);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 40);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 41);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 42);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 43);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 44);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 45);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 46);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 47);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 48);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 49);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 50);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 51);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 52);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 53);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 54);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 801);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 802);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 803);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 804);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 805);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 806);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 807);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 808);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 809);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 810);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 811);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 812);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 901);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 902);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1001);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1002);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1003);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1004);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1005);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1006);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1101);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1102);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1103);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1201);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1202);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1203);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1301);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1302);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1303);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1401);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1402);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1403);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1501);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1502);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1503);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1601);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1602);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1603);

            migrationBuilder.DropColumn(
                name: "HarvestEventId",
                table: "LaneSegments");

            migrationBuilder.DropColumn(
                name: "LotId",
                table: "LaneSegments");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "LaneSegments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HarvestEventId",
                table: "LaneSegments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LotId",
                table: "LaneSegments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrderId",
                table: "LaneSegments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReceiptDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReferenceCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerReceipts", x => x.Id);
                    table.CheckConstraint("CK_CustomerReceipt_Amount_Positive", "[Amount] > 0");
                    table.CheckConstraint("CK_CustomerReceipt_Amount_Thousands", "[Amount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_CustomerReceipts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaborSettlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PreparedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TotalObligation = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborSettlements", x => x.Id);
                    table.CheckConstraint("CK_LaborSettlement_TotalObligation_Thousands", "[TotalObligation] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Accounts_ApprovedByAccountId",
                        column: x => x.ApprovedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Accounts_PreparedByAccountId",
                        column: x => x.PreparedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Contractors_ContractorId",
                        column: x => x.ContractorId,
                        principalTable: "Contractors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborSettlements_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayRuleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DailyWageRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    EnteredByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MonthlySalary = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayRuleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayRuleVersions_Accounts_ApprovedByAccountId",
                        column: x => x.ApprovedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayRuleVersions_Accounts_EnteredByAccountId",
                        column: x => x.EnteredByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayRuleVersions_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayRuleVersions_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaborPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdjustmentReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AmountPaid = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReferenceCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborPayments", x => x.Id);
                    table.CheckConstraint("CK_LaborPayment_AmountPaid_Thousands", "[AmountPaid] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_LaborPayments_LaborPayments_OriginalPaymentId",
                        column: x => x.OriginalPaymentId,
                        principalTable: "LaborPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborPayments_LaborSettlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "LaborSettlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AttendanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayRuleVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LineType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayLines", x => x.Id);
                    table.CheckConstraint("CK_PayLine_Amount_Thousands", "[Amount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_PayLines_AttendanceRecords_AttendanceId",
                        column: x => x.AttendanceId,
                        principalTable: "AttendanceRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_LaborSettlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "LaborSettlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_PayRuleVersions_PayRuleVersionId",
                        column: x => x.PayRuleVersionId,
                        principalTable: "PayRuleVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_SeasonalWorkers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "SeasonalWorkers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PayLines_WorkAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "WorkAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerCredits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceAdjustmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceSaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerCredits", x => x.Id);
                    table.CheckConstraint("CK_CustomerCredit_Amount_Thousands", "[Amount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_CustomerCredits_CustomerReceipts_SourceReceiptId",
                        column: x => x.SourceReceiptId,
                        principalTable: "CustomerReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerCredits_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreditId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DocumentUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RefundDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TransferReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRefunds", x => x.Id);
                    table.CheckConstraint("CK_CustomerRefund_Amount_Positive", "[AmountPaid] > 0");
                    table.CheckConstraint("CK_CustomerRefund_AmountPaid_Thousands", "[AmountPaid] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_CustomerCredits_CreditId",
                        column: x => x.CreditId,
                        principalTable: "CustomerCredits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HarvestEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EstimatedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    HarvestDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LotPhotoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HarvestEvents_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestEvents_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestEvents_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MessageDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdjustmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetryOfAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SentByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ContentSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DocumentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DocumentVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProviderResponseCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Recipient = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageDeliveryAttempts", x => x.Id);
                    table.CheckConstraint("CK_MessageDeliveryAttempt_OneSource", "(CASE WHEN [OrderId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [SaleId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [AdjustmentId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [PublicationId] IS NULL THEN 0 ELSE 1 END + CASE WHEN [ReceiptId] IS NULL THEN 0 ELSE 1 END) = 1");
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_Accounts_SentByAccountId",
                        column: x => x.SentByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_CustomerReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "CustomerReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_MessageDeliveryAttempts_RetryOfAttemptId",
                        column: x => x.RetryOfAttemptId,
                        principalTable: "MessageDeliveryAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageDeliveryAttempts_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhysicalReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    AssessedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssessmentBasis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Condition = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HoldingDeadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HoldingPlace = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OtherDisposalQuantityKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedQuantityKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ResellableQuantityKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalReturns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhysicalReturns_Accounts_AssessedByAccountId",
                        column: x => x.AssessedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhysicalReturns_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PortHandovers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicationIdAtHandover = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WeighingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HandedOverAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsOffline = table.Column<bool>(type: "bit", nullable: false),
                    ReceivedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RecipientSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    SynchronizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortHandovers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortWeighings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BuyerConfirmation = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ConfirmedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Port = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WeighedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortWeighings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QualityClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    AffectedPortion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ComplaintType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Decision = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivingChannel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedHandling = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualityClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualityClaims_Accounts_DecidedByAccountId",
                        column: x => x.DecidedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityClaims_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualityClaims_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceiptAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptAllocations", x => x.Id);
                    table.CheckConstraint("CK_ReceiptAllocation_AllocatedAmount_Thousands", "[AllocatedAmount] % 1000 = 0");
                    table.CheckConstraint("CK_ReceiptAllocation_Amount_Positive", "[AllocatedAmount] > 0");
                    table.ForeignKey(
                        name: "FK_ReceiptAllocations_CustomerReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "CustomerReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NewWeighingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    AdjustmentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Decision = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    NegotiationBasis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Percentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleAdjustments", x => x.Id);
                    table.CheckConstraint("CK_SaleAdjustment_Amount_Thousands", "[Amount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_Accounts_ApprovedByAccountId",
                        column: x => x.ApprovedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_Accounts_DecidedByAccountId",
                        column: x => x.DecidedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_PortWeighings_NewWeighingId",
                        column: x => x.NewWeighingId,
                        principalTable: "PortWeighings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_QualityClaims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "QualityClaims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleAdjustments_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceReturnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgreedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AgreementBasis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AgreementChannel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ChannelEvidence = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GoodsSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OriginalTerms = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    QuantityAllocatedFromReturn = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestChannel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RequestedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RequiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TraderProposedChanges = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_PhysicalReturns_SourceReturnId",
                        column: x => x.SourceReturnId,
                        principalTable: "PhysicalReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TraceabilityLots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InitialOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisposalMethod = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DisposalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DisposedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DisposedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LotCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    QrCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceabilityLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraceabilityLots_Accounts_DisposedByAccountId",
                        column: x => x.DisposedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraceabilityLots_SalesOrders_InitialOrderId",
                        column: x => x.InitialOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WeighingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgreementBasis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsOfflineFinalized = table.Column<bool>(type: "bit", nullable: false),
                    PriceAgreedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    SalesReceiptUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleTransactions", x => x.Id);
                    table.CheckConstraint("CK_SaleTransaction_TotalAmount_Thousands", "[TotalAmount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_SaleTransactions_Accounts_PriceAgreedByAccountId",
                        column: x => x.PriceAgreedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleTransactions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleTransactions_PortWeighings_WeighingId",
                        column: x => x.WeighingId,
                        principalTable: "PortWeighings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleTransactions_SalesOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleTransactions_TraceabilityLots_LotId",
                        column: x => x.LotId,
                        principalTable: "TraceabilityLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TraceCorrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrectedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PublicationImpact = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraceCorrections_Accounts_ApprovedByAccountId",
                        column: x => x.ApprovedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraceCorrections_Accounts_CorrectedByAccountId",
                        column: x => x.CorrectedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraceCorrections_TraceabilityLots_LotId",
                        column: x => x.LotId,
                        principalTable: "TraceabilityLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TracePublicationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CooperativeSnapshot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublicContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewErrors = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewEvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReviewPassed = table.Column<bool>(type: "bit", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    WithdrawalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WithdrawnByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TracePublicationVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_Accounts_PublishedByAccountId",
                        column: x => x.PublishedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_Accounts_ReviewedByAccountId",
                        column: x => x.ReviewedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_Accounts_WithdrawnByAccountId",
                        column: x => x.WithdrawnByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TracePublicationVersions_TraceabilityLots_LotId",
                        column: x => x.LotId,
                        principalTable: "TraceabilityLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 801, "PENDING_REVIEW", null, "Pending Review", "SALES_ORDER", false, true, "The purchase request has just been sent and not yet reviewed by the cooperative.", 1 },
                    { 802, "AWAITING_CUSTOMER", null, "Awaiting Customer Agreement", "SALES_ORDER", false, false, "The cooperative proposes a change of quantity or time, awaiting the trader's agreement.", 2 },
                    { 803, "CONFIRMED", null, "Confirmed", "SALES_ORDER", false, false, "The cooperative has confirmed the product, quantity and order zone; the price is not yet agreed.", 3 },
                    { 804, "HARVESTING", null, "Harvesting", "SALES_ORDER", false, false, "A harvest plan or a first harvest exists for the order.", 4 },
                    { 805, "HARVESTED", null, "Harvested", "SALES_ORDER", false, false, "The actual harvest has been confirmed and the traceability lot created, or the order took over the lot of a cancelled order.", 5 },
                    { 806, "HANDED_OVER", null, "Handed Over", "SALES_ORDER", false, false, "The goods have been weighed and handed over at the port.", 6 },
                    { 807, "SOLD", null, "Sale Finalized", "SALES_ORDER", false, false, "The sale transaction has an agreed price and weight.", 7 },
                    { 808, "RETURN_REQUESTED", null, "Return Requested", "SALES_ORDER", false, false, "The trader has requested return of the whole received order, awaiting the Cooperative Owner's decision.", 8 },
                    { 809, "RETURN_ACCEPTED", null, "Return Accepted", "SALES_ORDER", false, false, "The Cooperative Owner accepted return of the whole order, awaiting the goods.", 9 },
                    { 810, "RETURNED", null, "Return Received", "SALES_ORDER", true, false, "The order's returned goods have arrived and been recorded.", 10 },
                    { 811, "REJECTED", null, "Rejected", "SALES_ORDER", true, false, "The cooperative rejected, or the trader did not agree to the proposed changes.", 11 },
                    { 812, "CANCELLED", null, "Cancelled", "SALES_ORDER", true, false, "The trader withdrew the request or cancelled the order before handover.", 12 },
                    { 901, "DRAFT", null, "Draft", "HARVEST_EVENT", false, true, "Lane portions and estimates are being recorded during the day.", 1 },
                    { 902, "CONFIRMED", null, "Confirmed", "HARVEST_EVENT", true, false, "Confirmed at the end of the day; later edits go through correction.", 2 },
                    { 1001, "DRAFT", null, "Draft", "TRACE_PUBLICATION", false, true, "The traceability record is being prepared or the review has not passed.", 1 },
                    { 1002, "PASSED", null, "Passed", "TRACE_PUBLICATION", false, false, "Review passed, awaiting publication.", 2 },
                    { 1003, "PUBLISHED", null, "Published", "TRACE_PUBLICATION", false, false, "Currently shown on the public QR page.", 3 },
                    { 1004, "NEEDS_REVIEW", null, "Needs Re-review", "TRACE_PUBLICATION", false, false, "Published data has just been corrected; no handover, resale or portal synchronization.", 4 },
                    { 1005, "SUPERSEDED", null, "Superseded", "TRACE_PUBLICATION", true, false, "Replaced by a newly published version.", 5 },
                    { 1006, "WITHDRAWN", null, "Withdrawn", "TRACE_PUBLICATION", true, false, "Publication withdrawn; the QR page shows the withdrawn status.", 6 },
                    { 1101, "PENDING", null, "Awaiting Decision", "QUALITY_CLAIM", false, true, "The claim has been submitted; the Cooperative Owner has not decided yet.", 1 },
                    { 1102, "IN_PROGRESS", null, "In Progress", "QUALITY_CLAIM", false, false, "A discount or return of the whole order has been decided and is being carried out.", 2 },
                    { 1103, "CLOSED", null, "Closed", "QUALITY_CLAIM", true, false, "Rejected, discount recorded, returned goods resolved, or return acceptance cancelled.", 3 },
                    { 1201, "HOLDING", null, "In Temporary Holding", "PHYSICAL_RETURN", false, true, "The returned goods have arrived and are held awaiting assessment.", 1 },
                    { 1202, "ASSESSED", null, "Assessed", "PHYSICAL_RETURN", false, false, "The resellable portion and the portion for other disposal have been assessed.", 2 },
                    { 1203, "DISPOSED", null, "Resolved", "PHYSICAL_RETURN", true, false, "The resellable portion is linked to new orders and the rest otherwise disposed of.", 3 },
                    { 1301, "DRAFT", null, "Draft", "SALE_ADJUSTMENT", false, true, "Awaiting the Cooperative Owner's approval; no effect on debt yet.", 1 },
                    { 1302, "APPROVED", null, "Approved", "SALE_ADJUSTMENT", true, false, "Approved; changes the receivable or records a credit/amount to refund.", 2 },
                    { 1303, "REJECTED", null, "Rejected", "SALE_ADJUSTMENT", true, false, "Not approved; debt unchanged.", 3 },
                    { 1401, "DRAFT", null, "Draft", "PAY_RULE", false, true, "A new salary or wage rate awaiting approval; not yet used for calculation.", 1 },
                    { 1402, "APPROVED", null, "Approved", "PAY_RULE", true, false, "Approved; used from the effective date.", 2 },
                    { 1403, "REJECTED", null, "Rejected", "PAY_RULE", true, false, "Not approved; the current rate stays unchanged.", 3 },
                    { 1501, "DRAFT", null, "Draft", "LABOR_SETTLEMENT", false, true, "The timesheet or payroll is being prepared, awaiting approval.", 1 },
                    { 1502, "APPROVED", null, "Approved", "LABOR_SETTLEMENT", false, false, "Approved by the Cooperative Owner; the basis for payment.", 2 },
                    { 1503, "PAID", null, "Fully Paid", "LABOR_SETTLEMENT", true, false, "Total paid equals the approved obligation.", 3 },
                    { 1601, "SENDING", null, "Sending", "MESSAGE_DELIVERY", false, true, "Handed to the channel provider, no response yet.", 1 },
                    { 1602, "SENT", null, "Sent", "MESSAGE_DELIVERY", true, false, "The provider reports successful delivery.", 2 },
                    { 1603, "FAILED", null, "Failed", "MESSAGE_DELIVERY", true, false, "The provider reports an error or the response timeout expires.", 3 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 12, "Cooperative Owner", 801, "Confirm the product, quantity and order zone.", false, 803, "Action", "UC44" },
                    { 13, "Cooperative Owner", 801, "Propose a change of quantity or time.", false, 802, "Action", "UC44" },
                    { 14, "Cooperative Owner", 801, "The cooperative cannot fulfill.", true, 811, "Action", "UC44" },
                    { 15, "Cooperative Owner", 801, "The trader withdraws the purchase request before review.", true, 812, "Action", "UC44" },
                    { 16, "Cooperative Owner", 802, "The trader agrees; channel, time and basis of agreement recorded.", false, 803, "Action", "UC44" },
                    { 17, "Cooperative Owner", 802, "The trader does not agree.", true, 811, "Action", "UC44" },
                    { 18, "Cooperative Owner", 802, "The trader withdraws the request while changes await agreement.", true, 812, "Action", "UC44" },
                    { 19, "Cooperative Owner", 803, "The trader cancels the order.", true, 812, "Action", "UC44" },
                    { 20, "Cooperative Owner; Zone Manager", 803, "Plan the harvest, or record the first harvest when no plan is made.", false, 804, "Action", "UC30, UC31" },
                    { 21, "Cooperative Owner; Zone Manager", 804, "Harvest Done selected; every harvest of the order is confirmed.", false, 805, "Action", "UC31" },
                    { 22, "Cooperative Owner", 804, "The trader drops the order during harvest.", true, 812, "Action", "UC44" },
                    { 23, "Cooperative Owner", 805, "The trader drops the order after harvest, before handover.", true, 812, "Action", "UC44" },
                    { 24, "Cooperative Owner; Zone Manager", 805, "Weigh and hand over at the port.", false, 806, "Action", "UC45" },
                    { 25, "Cooperative Owner; Zone Manager", 803, "Resale order of returned goods: no harvest involved.", false, 806, "Action", "UC45" },
                    { 26, "Cooperative Owner", 803, "An order without a lot takes over the harvested lot of a cancelled order of the same product.", false, 805, "Action", "UC44" },
                    { 27, "Cooperative Owner; Zone Manager", 806, "Agree the price and weight.", false, 807, "Action", "UC48" },
                    { 28, "Trader; Cooperative Owner", 807, "Within 24 hours of sale finalization; no Draft/Approved discount and no return request yet.", false, 808, "Action", "UC51" },
                    { 29, "Cooperative Owner", 808, "Accept return of the whole order; the trader is notified.", false, 809, "Action", "UC52" },
                    { 30, "Cooperative Owner", 808, "Reject the return or decide on a discount instead.", true, 807, "Action", "UC52" },
                    { 31, "Cooperative Owner; Zone Manager", 809, "The returned goods have arrived and are recorded as received.", false, 810, "Action", "UC52" },
                    { 32, "Cooperative Owner", 809, "Trader unreachable or goods not returned: cancel the return acceptance.", true, 807, "Action", "UC52" },
                    { 33, "Cooperative Owner; Zone Manager", 901, "Confirm at the end of the harvest day.", false, 902, "Action", "UC31" },
                    { 34, "Cooperative Owner; Zone Manager", 1001, "Review passed.", false, 1002, "Action", "UC32" },
                    { 35, "Cooperative Owner; Zone Manager", 1002, "Publish the passed version.", false, 1003, "Action", "UC32" },
                    { 36, "System", 1002, "The lot's source data changed after review and before publication.", false, 1001, "System", "UC32" },
                    { 37, "System", 1003, "A correction of source data or weight affects published content.", false, 1004, "System", "UC39" },
                    { 38, "Cooperative Owner; Zone Manager", 1003, "Published weight or source proven wrong, or the lot can no longer be used.", true, 1006, "Action", "UC32" },
                    { 39, "System", 1004, "A new version of the same lot is published.", false, 1005, "System", "UC32" },
                    { 40, "Cooperative Owner; Zone Manager", 1004, "Published weight or source proven wrong, or the lot can no longer be used.", true, 1006, "Action", "UC32" },
                    { 41, "Cooperative Owner", 1101, "Reject the claim.", true, 1103, "Action", "UC52" },
                    { 42, "Cooperative Owner", 1101, "Decide on a discount, or accept return of the whole order.", false, 1102, "Action", "UC52" },
                    { 43, "System", 1102, "Discount approved/rejected, returned goods resolved, or return acceptance cancelled.", false, 1103, "System", "UC53, UC52" },
                    { 44, "Cooperative Owner", 1201, "Assess the resellable portion.", false, 1202, "Action", "UC52" },
                    { 45, "System", 1202, "All resold or other disposal recorded.", false, 1203, "System", "UC52" },
                    { 46, "Cooperative Owner", 1301, "The total decrease does not exceed the remaining adjustable amount.", false, 1302, "Action", "UC53, UC39" },
                    { 47, "Cooperative Owner", 1301, "The Cooperative Owner does not approve the adjustment.", true, 1303, "Action", "UC53, UC39" },
                    { 48, "Cooperative Owner", 1401, "The effective period does not overlap an approved rate of the same subject.", false, 1402, "Action", "UC58" },
                    { 49, "Cooperative Owner", 1401, "The Cooperative Owner does not approve the new rate.", true, 1403, "Action", "UC58" },
                    { 50, "Cooperative Owner", 1501, "Review and approve the sheet.", false, 1502, "Action", "UC60" },
                    { 51, "Cooperative Owner", 1502, "Reopen for editing; audit log written (before – after).", true, 1501, "Action", "UC60" },
                    { 52, "System", 1502, "Total paid equals the approved obligation.", false, 1503, "System", "UC61" },
                    { 53, "System", 1601, "Success response from Zalo OA, Email Service or SMS Service.", false, 1602, "System", "UC70" },
                    { 54, "System", 1601, "Error response or timeout.", false, 1603, "System; time", "UC70" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_HarvestEventId",
                table: "LaneSegments",
                column: "HarvestEventId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_LotId",
                table: "LaneSegments",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_OrderId",
                table: "LaneSegments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCredits_CustomerId",
                table: "CustomerCredits",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCredits_SourceAdjustmentId",
                table: "CustomerCredits",
                column: "SourceAdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCredits_SourceReceiptId",
                table: "CustomerCredits",
                column: "SourceReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCredits_SourceSaleId",
                table: "CustomerCredits",
                column: "SourceSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerReceipts_CustomerId",
                table: "CustomerReceipts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerReceipts_DeduplicationKey",
                table: "CustomerReceipts",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerReceipts_Method_ReferenceCode",
                table: "CustomerReceipts",
                columns: new[] { "Method", "ReferenceCode" },
                unique: true,
                filter: "[ReferenceCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_CreditId",
                table: "CustomerRefunds",
                column: "CreditId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_CustomerId",
                table: "CustomerRefunds",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_DeduplicationKey",
                table: "CustomerRefunds",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestEvents_OrderId",
                table: "HarvestEvents",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestEvents_RecordedByAccountId",
                table: "HarvestEvents",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestEvents_StatusId",
                table: "HarvestEvents",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestEvents_ZoneId",
                table: "HarvestEvents",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborPayments_OriginalPaymentId",
                table: "LaborPayments",
                column: "OriginalPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborPayments_SettlementId",
                table: "LaborPayments",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_ApprovedByAccountId",
                table: "LaborSettlements",
                column: "ApprovedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_ContractorId",
                table: "LaborSettlements",
                column: "ContractorId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_EmployeeId",
                table: "LaborSettlements",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_PreparedByAccountId",
                table: "LaborSettlements",
                column: "PreparedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborSettlements_StatusId",
                table: "LaborSettlements",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_AdjustmentId",
                table: "MessageDeliveryAttempts",
                column: "AdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_CustomerId",
                table: "MessageDeliveryAttempts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_OrderId",
                table: "MessageDeliveryAttempts",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_PublicationId",
                table: "MessageDeliveryAttempts",
                column: "PublicationId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_ReceiptId",
                table: "MessageDeliveryAttempts",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_RetryOfAttemptId",
                table: "MessageDeliveryAttempts",
                column: "RetryOfAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_SaleId",
                table: "MessageDeliveryAttempts",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_SentByAccountId",
                table: "MessageDeliveryAttempts",
                column: "SentByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveryAttempts_StatusId",
                table: "MessageDeliveryAttempts",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_AssignmentId",
                table: "PayLines",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_AttendanceId",
                table: "PayLines",
                column: "AttendanceId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_EmployeeId",
                table: "PayLines",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_PayRuleVersionId",
                table: "PayLines",
                column: "PayRuleVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_SettlementId",
                table: "PayLines",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_PayLines_WorkerId",
                table: "PayLines",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_ApprovedByAccountId",
                table: "PayRuleVersions",
                column: "ApprovedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_EmployeeId",
                table: "PayRuleVersions",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_EnteredByAccountId",
                table: "PayRuleVersions",
                column: "EnteredByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_StatusId",
                table: "PayRuleVersions",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_PayRuleVersions_SubjectType_EmployeeId_ValidFrom",
                table: "PayRuleVersions",
                columns: new[] { "SubjectType", "EmployeeId", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalReturns_AssessedByAccountId",
                table: "PhysicalReturns",
                column: "AssessedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalReturns_LotId",
                table: "PhysicalReturns",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalReturns_SaleId",
                table: "PhysicalReturns",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalReturns_StatusId",
                table: "PhysicalReturns",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_PortHandovers_OrderId",
                table: "PortHandovers",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PortHandovers_PublicationIdAtHandover",
                table: "PortHandovers",
                column: "PublicationIdAtHandover");

            migrationBuilder.CreateIndex(
                name: "IX_PortHandovers_WeighingId",
                table: "PortHandovers",
                column: "WeighingId");

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighings_OrderId",
                table: "PortWeighings",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_DecidedByAccountId",
                table: "QualityClaims",
                column: "DecidedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_RecordedByAccountId",
                table: "QualityClaims",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_SaleId",
                table: "QualityClaims",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_StatusId",
                table: "QualityClaims",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptAllocations_ReceiptId",
                table: "ReceiptAllocations",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptAllocations_SaleId",
                table: "ReceiptAllocations",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_ApprovedByAccountId",
                table: "SaleAdjustments",
                column: "ApprovedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_ClaimId",
                table: "SaleAdjustments",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_DecidedByAccountId",
                table: "SaleAdjustments",
                column: "DecidedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_NewWeighingId",
                table: "SaleAdjustments",
                column: "NewWeighingId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_SaleId",
                table: "SaleAdjustments",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleAdjustments_StatusId",
                table: "SaleAdjustments",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_Code",
                table: "SalesOrders",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CustomerId_StatusId",
                table: "SalesOrders",
                columns: new[] { "CustomerId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_ProductId",
                table: "SalesOrders",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_RecordedByAccountId",
                table: "SalesOrders",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_SourceLotId",
                table: "SalesOrders",
                column: "SourceLotId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_SourceReturnId",
                table: "SalesOrders",
                column: "SourceReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_StatusId",
                table: "SalesOrders",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_ZoneId_StatusId",
                table: "SalesOrders",
                columns: new[] { "ZoneId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_Code",
                table: "SaleTransactions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_CustomerId",
                table: "SaleTransactions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_DeduplicationKey",
                table: "SaleTransactions",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_LotId",
                table: "SaleTransactions",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_OrderId",
                table: "SaleTransactions",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_PriceAgreedByAccountId",
                table: "SaleTransactions",
                column: "PriceAgreedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_WeighingId",
                table: "SaleTransactions",
                column: "WeighingId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityLots_DisposedByAccountId",
                table: "TraceabilityLots",
                column: "DisposedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityLots_InitialOrderId",
                table: "TraceabilityLots",
                column: "InitialOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityLots_LotCode",
                table: "TraceabilityLots",
                column: "LotCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraceabilityLots_QrCode",
                table: "TraceabilityLots",
                column: "QrCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraceCorrections_ApprovedByAccountId",
                table: "TraceCorrections",
                column: "ApprovedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceCorrections_CorrectedByAccountId",
                table: "TraceCorrections",
                column: "CorrectedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceCorrections_LotId",
                table: "TraceCorrections",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_LotId_VersionNumber",
                table: "TracePublicationVersions",
                columns: new[] { "LotId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_PublishedByAccountId",
                table: "TracePublicationVersions",
                column: "PublishedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_ReviewedByAccountId",
                table: "TracePublicationVersions",
                column: "ReviewedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_StatusId",
                table: "TracePublicationVersions",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_TracePublicationVersions_WithdrawnByAccountId",
                table: "TracePublicationVersions",
                column: "WithdrawnByAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_LaneSegments_HarvestEvents_HarvestEventId",
                table: "LaneSegments",
                column: "HarvestEventId",
                principalTable: "HarvestEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaneSegments_SalesOrders_OrderId",
                table: "LaneSegments",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaneSegments_TraceabilityLots_LotId",
                table: "LaneSegments",
                column: "LotId",
                principalTable: "TraceabilityLots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerCredits_SaleAdjustments_SourceAdjustmentId",
                table: "CustomerCredits",
                column: "SourceAdjustmentId",
                principalTable: "SaleAdjustments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerCredits_SaleTransactions_SourceSaleId",
                table: "CustomerCredits",
                column: "SourceSaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestEvents_SalesOrders_OrderId",
                table: "HarvestEvents",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageDeliveryAttempts_SaleAdjustments_AdjustmentId",
                table: "MessageDeliveryAttempts",
                column: "AdjustmentId",
                principalTable: "SaleAdjustments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageDeliveryAttempts_SaleTransactions_SaleId",
                table: "MessageDeliveryAttempts",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageDeliveryAttempts_SalesOrders_OrderId",
                table: "MessageDeliveryAttempts",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageDeliveryAttempts_TracePublicationVersions_PublicationId",
                table: "MessageDeliveryAttempts",
                column: "PublicationId",
                principalTable: "TracePublicationVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PhysicalReturns_SaleTransactions_SaleId",
                table: "PhysicalReturns",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PhysicalReturns_TraceabilityLots_LotId",
                table: "PhysicalReturns",
                column: "LotId",
                principalTable: "TraceabilityLots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PortHandovers_PortWeighings_WeighingId",
                table: "PortHandovers",
                column: "WeighingId",
                principalTable: "PortWeighings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PortHandovers_SalesOrders_OrderId",
                table: "PortHandovers",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PortHandovers_TracePublicationVersions_PublicationIdAtHandover",
                table: "PortHandovers",
                column: "PublicationIdAtHandover",
                principalTable: "TracePublicationVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PortWeighings_SalesOrders_OrderId",
                table: "PortWeighings",
                column: "OrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QualityClaims_SaleTransactions_SaleId",
                table: "QualityClaims",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiptAllocations_SaleTransactions_SaleId",
                table: "ReceiptAllocations",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SaleAdjustments_SaleTransactions_SaleId",
                table: "SaleAdjustments",
                column: "SaleId",
                principalTable: "SaleTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesOrders_TraceabilityLots_SourceLotId",
                table: "SalesOrders",
                column: "SourceLotId",
                principalTable: "TraceabilityLots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
