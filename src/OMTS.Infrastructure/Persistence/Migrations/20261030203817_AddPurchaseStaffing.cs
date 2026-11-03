using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseStaffing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 77);

            migrationBuilder.AddColumn<int>(
                name: "CagesPerEmployeePerDay",
                table: "PurchaseRequest",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlannedCages",
                table: "PurchaseRequest",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PlannedPickupDate",
                table: "PurchaseRequest",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaffingNote",
                table: "PurchaseRequest",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WorkDate",
                table: "PurchaseRequest",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PurchaseShiftInvitation",
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
                    table.PrimaryKey("PK_PurchaseShiftInvitation", x => x.Id);
                    table.CheckConstraint("CK_PurchaseShiftInvitation_Reservation", "[ReservedWorkDate] IS NULL OR ([Approved] IS NOT NULL AND [Approved] = 1 AND [RespondedAt] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PurchaseShiftInvitation_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseShiftInvitation_PurchaseRequest_PurchaseRequestId",
                        column: x => x.PurchaseRequestId,
                        principalTable: "PurchaseRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[] { 1905, "AWAITING_STAFF", null, "Awaiting staff confirmation", "PURCHASE_REQUEST", false, false, "User decision 31/10/2026: employees approve their shift before the Owner accepts the request.", 5 });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 81, "Cooperative Owner", 1901, "The Owner sends dated shift invitations with a cage capacity.", false, 1905, "Action", "SALE-03" },
                    { 82, "Cooperative Owner", 1905, "The Owner declines with a reason.", true, 1903, "Action", "SALE-03" },
                    { 83, "Trader", 1905, "Only the requesting customer may cancel; release shift reservations.", false, 1904, "Action", "SALE-02" },
                    { 84, "Cooperative Owner", 1905, "Enough distinct employees have approved the dated shift; cages and pickup match the staffing plan.", false, 1902, "Action", "SALE-03" }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_Staffing",
                table: "PurchaseRequest",
                sql: "([PlannedCages] IS NULL AND [WorkDate] IS NULL AND [PlannedPickupDate] IS NULL AND [CagesPerEmployeePerDay] IS NULL) OR ([PlannedCages] IS NOT NULL AND [PlannedCages] > 0 AND [PlannedCages] <= [RequestedCages] AND [WorkDate] IS NOT NULL AND [PlannedPickupDate] IS NOT NULL AND [WorkDate] <= [PlannedPickupDate] AND [CagesPerEmployeePerDay] IS NOT NULL AND [CagesPerEmployeePerDay] > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseShiftInvitation_EmployeeId_ReservedWorkDate",
                table: "PurchaseShiftInvitation",
                columns: new[] { "EmployeeId", "ReservedWorkDate" },
                unique: true,
                filter: "[ReservedWorkDate] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseShiftInvitation_PurchaseRequestId_EmployeeId",
                table: "PurchaseShiftInvitation",
                columns: new[] { "PurchaseRequestId", "EmployeeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseShiftInvitation");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_Staffing",
                table: "PurchaseRequest");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 81);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 82);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 83);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 84);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1905);

            migrationBuilder.DropColumn(
                name: "CagesPerEmployeePerDay",
                table: "PurchaseRequest");

            migrationBuilder.DropColumn(
                name: "PlannedCages",
                table: "PurchaseRequest");

            migrationBuilder.DropColumn(
                name: "PlannedPickupDate",
                table: "PurchaseRequest");

            migrationBuilder.DropColumn(
                name: "StaffingNote",
                table: "PurchaseRequest");

            migrationBuilder.DropColumn(
                name: "WorkDate",
                table: "PurchaseRequest");

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 77, "Cooperative Owner", 1901, "Positive accepted cage count no larger than requested; pickup date is today or later.", false, 1902, "Action", "SALE-03" });
        }
    }
}
