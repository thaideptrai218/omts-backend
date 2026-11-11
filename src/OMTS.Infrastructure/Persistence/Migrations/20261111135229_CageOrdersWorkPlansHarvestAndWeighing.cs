using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CageOrdersWorkPlansHarvestAndWeighing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_Accepted_Has_Response",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_AcceptedKg_Range",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_RequestedKg_Positive",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Product_UnitPrice_Positive",
                table: "Products");

            migrationBuilder.AddColumn<Guid>(
                name: "PortWeighingId",
                table: "SalesInvoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AcceptedCages",
                table: "PurchaseRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CagesPerEmployeePerDay",
                table: "PurchaseRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientOperationId",
                table: "PurchaseRequests",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "PurchaseRequests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HarvestCompletedAt",
                table: "PurchaseRequests",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HarvestCompletedByAccountId",
                table: "PurchaseRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlannedCages",
                table: "PurchaseRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PlannedPickupDate",
                table: "PurchaseRequests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductName",
                table: "PurchaseRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RequestedCages",
                table: "PurchaseRequests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StaffingNote",
                table: "PurchaseRequests",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WorkDate",
                table: "PurchaseRequests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Products",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Products",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ValidFrom",
                table: "Products",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ValidTo",
                table: "Products",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseRequestId",
                table: "HarvestLots",
                type: "uniqueidentifier",
                nullable: true);

            // Requests entered in kg become whole cages at the cooperative's average kg per cage, rounded up so no request shrinks (decision of
            // 08/11/2026). Each request gets the code the screens name it by and the name of its product as it was sent.
            migrationBuilder.Sql("""
                DECLARE @kgPerCage decimal(18,3) = (SELECT TOP 1 NULLIF([AverageKgPerCage], 0) FROM [Cooperatives]);
                SET @kgPerCage = ISNULL(@kgPerCage, 50);
                UPDATE r SET
                    [RequestedCages] = CAST(CEILING(r.[RequestedKg] / @kgPerCage) AS int),
                    [AcceptedCages] = CASE WHEN r.[AcceptedKg] IS NULL THEN NULL ELSE CAST(CEILING(r.[AcceptedKg] / @kgPerCage) AS int) END,
                    [ProductName] = p.[Name],
                    [Code] = N'YC-' + CONVERT(nchar(8), SWITCHOFFSET(r.[SentAt], '+07:00'), 112) + N'-' + UPPER(LEFT(REPLACE(CONVERT(nvarchar(36), r.[Id]), N'-', N''), 12))
                FROM [PurchaseRequests] r
                JOIN [Products] p ON p.[Id] = r.[ProductId];
                """);

            migrationBuilder.DropColumn(
                name: "AcceptedKg",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "RequestedKg",
                table: "PurchaseRequests");

            migrationBuilder.CreateTable(
                name: "PortWeighings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WeighedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WeighedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfirmedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortWeighings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortWeighings_Accounts_ConfirmedByAccountId",
                        column: x => x.ConfirmedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortWeighings_Accounts_WeighedByAccountId",
                        column: x => x.WeighedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortWeighings_PurchaseRequests_PurchaseRequestId",
                        column: x => x.PurchaseRequestId,
                        principalTable: "PurchaseRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseShiftInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Approved = table.Column<bool>(type: "bit", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReservedWorkDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseShiftInvitations", x => x.Id);
                    table.CheckConstraint("CK_PurchaseShiftInvitation_Reservation", "[ReservedWorkDate] IS NULL OR ([Approved] IS NOT NULL AND [Approved] = 1 AND [RespondedAt] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PurchaseShiftInvitations_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseShiftInvitations_PurchaseRequests_PurchaseRequestId",
                        column: x => x.PurchaseRequestId,
                        principalTable: "PurchaseRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkDayPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResponsibleAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PurchaseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClientOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Approved = table.Column<bool>(type: "bit", nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResponseReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDayPlans", x => x.Id);
                    table.CheckConstraint("CK_WorkDayPlan_Response", "([Approved] IS NULL AND [RespondedAt] IS NULL) OR ([Approved] IS NOT NULL AND [RespondedAt] IS NOT NULL AND ([Approved] = 1 OR ([ResponseReason] IS NOT NULL AND LEN(LTRIM(RTRIM([ResponseReason]))) > 0)))");
                    table.ForeignKey(
                        name: "FK_WorkDayPlans_Accounts_CreatorAccountId",
                        column: x => x.CreatorAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlans_Accounts_ResponsibleAccountId",
                        column: x => x.ResponsibleAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlans_JobTypes_JobTypeId",
                        column: x => x.JobTypeId,
                        principalTable: "JobTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlans_PurchaseRequests_PurchaseRequestId",
                        column: x => x.PurchaseRequestId,
                        principalTable: "PurchaseRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDayPlans_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PortWeighingCages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PortWeighingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CageNumber = table.Column<int>(type: "int", nullable: false),
                    HarvestLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortWeighingCages", x => x.Id);
                    table.CheckConstraint("CK_PortWeighingCage_Kg", "[Kg] > 0");
                    table.CheckConstraint("CK_PortWeighingCage_Number", "[CageNumber] >= 1");
                    table.ForeignKey(
                        name: "FK_PortWeighingCages_HarvestLots_HarvestLotId",
                        column: x => x.HarvestLotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortWeighingCages_PortWeighings_PortWeighingId",
                        column: x => x.PortWeighingId,
                        principalTable: "PortWeighings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 180,
                column: "GuardCondition",
                value: "The accepted cages are above 0 and not more than the requested cages; the pickup date is not before today.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3202,
                column: "Meaning",
                value: "Accepted by the Cooperative Owner, possibly for fewer cages; invoices may be linked to it.");

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[] { 3205, "AWAITING_STAFF", null, "Awaiting staff confirmation", "PURCHASE_REQUEST", false, false, "User decision 31/10/2026: employees approve their shift before the Owner accepts the request.", 5 });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30082, "Cooperative Owner", 3201, "The Owner sends dated shift invitations with a cage capacity.", false, 3205, "Action", "SALE-03" },
                    { 30083, "Cooperative Owner", 3205, "The Owner declines with a reason.", true, 3203, "Action", "SALE-03" },
                    { 30084, "Trader", 3205, "Only the requesting customer may cancel; release shift reservations.", false, 3204, "Action", "SALE-02" },
                    { 30085, "Cooperative Owner", 3205, "Enough distinct employees have approved the dated shift; cages and pickup match the staffing plan.", false, 3202, "Action", "SALE-03" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_PortWeighingId",
                table: "SalesInvoices",
                column: "PortWeighingId",
                unique: true,
                filter: "[PortWeighingId] IS NOT NULL AND [StatusId] = 3501");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SalesInvoice_Weighing_Has_Request",
                table: "SalesInvoices",
                sql: "[PortWeighingId] IS NULL OR [PurchaseRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_Code",
                table: "PurchaseRequests",
                column: "Code",
                unique: true,
                filter: "[Code] <> N''");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_CustomerId_ClientOperationId",
                table: "PurchaseRequests",
                columns: new[] { "CustomerId", "ClientOperationId" },
                unique: true,
                filter: "[ClientOperationId] <> '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_CustomerId_CreatedAt",
                table: "PurchaseRequests",
                columns: new[] { "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_HarvestCompletedByAccountId",
                table: "PurchaseRequests",
                column: "HarvestCompletedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_StatusId_CreatedAt",
                table: "PurchaseRequests",
                columns: new[] { "StatusId", "CreatedAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Accepted_Has_Response",
                table: "PurchaseRequests",
                sql: "[StatusId] <> 3202 OR ([AcceptedCages] IS NOT NULL AND [AgreedPickupDate] IS NOT NULL AND [PayLaterAgreed] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Cages",
                table: "PurchaseRequests",
                sql: "[RequestedCages] > 0 AND ([AcceptedCages] IS NULL OR ([AcceptedCages] > 0 AND [AcceptedCages] <= [RequestedCages]))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_HarvestCompleted",
                table: "PurchaseRequests",
                sql: "([HarvestCompletedAt] IS NULL AND [HarvestCompletedByAccountId] IS NULL) OR ([HarvestCompletedAt] IS NOT NULL AND [HarvestCompletedByAccountId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Staffing",
                table: "PurchaseRequests",
                sql: "([PlannedCages] IS NULL AND [WorkDate] IS NULL AND [PlannedPickupDate] IS NULL AND [CagesPerEmployeePerDay] IS NULL) OR ([PlannedCages] IS NOT NULL AND [PlannedCages] > 0 AND [PlannedCages] <= [RequestedCages] AND [WorkDate] IS NOT NULL AND [PlannedPickupDate] IS NOT NULL AND [WorkDate] <= [PlannedPickupDate] AND [CagesPerEmployeePerDay] IS NOT NULL AND [CagesPerEmployeePerDay] > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Code",
                table: "Products",
                column: "Code",
                unique: true,
                filter: "[Code] <> N''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Product_UnitPrice_Positive",
                table: "Products",
                sql: "[UnitPrice] > 0 OR ([Code] <> N'' AND [UnitPrice] = 0)");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_PurchaseRequestId",
                table: "HarvestLots",
                column: "PurchaseRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighingCages_HarvestLotId",
                table: "PortWeighingCages",
                column: "HarvestLotId");

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighingCages_PortWeighingId_CageNumber",
                table: "PortWeighingCages",
                columns: new[] { "PortWeighingId", "CageNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighings_ConfirmedByAccountId",
                table: "PortWeighings",
                column: "ConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighings_PurchaseRequestId_WeighedAt",
                table: "PortWeighings",
                columns: new[] { "PurchaseRequestId", "WeighedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighings_WeighedByAccountId",
                table: "PortWeighings",
                column: "WeighedByAccountId");

            migrationBuilder.CreateIndex(
                name: "UX_PortWeighing_One_Unconfirmed_Per_Request",
                table: "PortWeighings",
                column: "PurchaseRequestId",
                unique: true,
                filter: "[ConfirmedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseShiftInvitations_EmployeeId_ReservedWorkDate",
                table: "PurchaseShiftInvitations",
                columns: new[] { "EmployeeId", "ReservedWorkDate" },
                unique: true,
                filter: "[ReservedWorkDate] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseShiftInvitations_PurchaseRequestId_EmployeeId",
                table: "PurchaseShiftInvitations",
                columns: new[] { "PurchaseRequestId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_CreatorAccountId_ClientOperationId",
                table: "WorkDayPlans",
                columns: new[] { "CreatorAccountId", "ClientOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_JobTypeId",
                table: "WorkDayPlans",
                column: "JobTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_PurchaseRequestId",
                table: "WorkDayPlans",
                column: "PurchaseRequestId",
                unique: true,
                filter: "[PurchaseRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_RaftId",
                table: "WorkDayPlans",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDayPlans_ResponsibleAccountId_WorkDate",
                table: "WorkDayPlans",
                columns: new[] { "ResponsibleAccountId", "WorkDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestLots_PurchaseRequests_PurchaseRequestId",
                table: "HarvestLots",
                column: "PurchaseRequestId",
                principalTable: "PurchaseRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseRequests_Accounts_HarvestCompletedByAccountId",
                table: "PurchaseRequests",
                column: "HarvestCompletedByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoices_PortWeighings_PortWeighingId",
                table: "SalesInvoices",
                column: "PortWeighingId",
                principalTable: "PortWeighings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HarvestLots_PurchaseRequests_PurchaseRequestId",
                table: "HarvestLots");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseRequests_Accounts_HarvestCompletedByAccountId",
                table: "PurchaseRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoices_PortWeighings_PortWeighingId",
                table: "SalesInvoices");

            migrationBuilder.DropTable(
                name: "PortWeighingCages");

            migrationBuilder.DropTable(
                name: "PurchaseShiftInvitations");

            migrationBuilder.DropTable(
                name: "WorkDayPlans");

            migrationBuilder.DropTable(
                name: "PortWeighings");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_PortWeighingId",
                table: "SalesInvoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SalesInvoice_Weighing_Has_Request",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_Code",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_CustomerId_ClientOperationId",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_CustomerId_CreatedAt",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_HarvestCompletedByAccountId",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_StatusId_CreatedAt",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_Accepted_Has_Response",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_Cages",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_HarvestCompleted",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_Staffing",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_Products_Code",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Product_UnitPrice_Positive",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLots_PurchaseRequestId",
                table: "HarvestLots");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyColumnType: "int",
                keyValue: 30082);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyColumnType: "int",
                keyValue: 30083);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyColumnType: "int",
                keyValue: 30084);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyColumnType: "int",
                keyValue: 30085);

            migrationBuilder.Sql("UPDATE [PurchaseRequests] SET [StatusId] = 3201 WHERE [StatusId] = 3205;");

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyColumnType: "int",
                keyValue: 3205);

            migrationBuilder.DropColumn(
                name: "PortWeighingId",
                table: "SalesInvoices");

            migrationBuilder.AddColumn<decimal>(
                name: "AcceptedKg",
                table: "PurchaseRequests",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RequestedKg",
                table: "PurchaseRequests",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql("""
                DECLARE @kgPerCage decimal(18,3) = (SELECT TOP 1 NULLIF([AverageKgPerCage], 0) FROM [Cooperatives]);
                SET @kgPerCage = ISNULL(@kgPerCage, 50);
                UPDATE [PurchaseRequests] SET
                    [RequestedKg] = [RequestedCages] * @kgPerCage,
                    [AcceptedKg] = CASE WHEN [AcceptedCages] IS NULL THEN NULL ELSE [AcceptedCages] * @kgPerCage END;
                """);

            migrationBuilder.DropColumn(
                name: "AcceptedCages",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "CagesPerEmployeePerDay",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "ClientOperationId",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "HarvestCompletedAt",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "HarvestCompletedByAccountId",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "PlannedCages",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "PlannedPickupDate",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "ProductName",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "RequestedCages",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "StaffingNote",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "WorkDate",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ValidTo",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PurchaseRequestId",
                table: "HarvestLots");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumns: ["Id"],
                keyColumnTypes: ["int"],
                keyValues: [180],
                columns: ["GuardCondition"],
                columnTypes: ["nvarchar(1000)"],
                values: ["The accepted kg is above 0 and not more than the requested kg; the pickup date is not before today."]);

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumns: ["Id"],
                keyColumnTypes: ["int"],
                keyValues: [3202],
                columns: ["Meaning"],
                columnTypes: ["nvarchar(1000)"],
                values: ["Accepted by the Cooperative Owner, possibly for fewer kg; invoices may be linked to it."]);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Accepted_Has_Response",
                table: "PurchaseRequests",
                sql: "[StatusId] <> 3202 OR ([AcceptedKg] IS NOT NULL AND [AgreedPickupDate] IS NOT NULL AND [PayLaterAgreed] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_AcceptedKg_Range",
                table: "PurchaseRequests",
                sql: "[AcceptedKg] IS NULL OR ([AcceptedKg] > 0 AND [AcceptedKg] <= [RequestedKg])");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_RequestedKg_Positive",
                table: "PurchaseRequests",
                sql: "[RequestedKg] > 0");

            // A sales product of the catalogue may be priced 0, which the older check refuses: the check comes back without checking those rows.
            migrationBuilder.Sql("ALTER TABLE [Products] WITH NOCHECK ADD CONSTRAINT [CK_Product_UnitPrice_Positive] CHECK ([UnitPrice] > 0);");
        }
    }
}
