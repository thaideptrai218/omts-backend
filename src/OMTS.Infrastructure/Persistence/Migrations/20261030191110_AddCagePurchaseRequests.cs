using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCagePurchaseRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedCages = table.Column<int>(type: "int", nullable: false),
                    PickupDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ClientOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    AcceptedCages = table.Column<int>(type: "int", nullable: true),
                    AcceptedPickupDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AllowPayLater = table.Column<bool>(type: "bit", nullable: false),
                    ResponseNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RespondedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequest", x => x.Id);
                    table.CheckConstraint("CK_PurchaseRequest_Cages", "[RequestedCages] > 0 AND ([AcceptedCages] IS NULL OR ([AcceptedCages] > 0 AND [AcceptedCages] <= [RequestedCages]))");
                    table.ForeignKey(
                        name: "FK_PurchaseRequest_Accounts_RespondedByAccountId",
                        column: x => x.RespondedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseRequest_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseRequest_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseRequest_Statuses_StatusId",
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
                    { 3901, "AWAITING_RESPONSE", null, "Awaiting response", "LEGACY_CAGE_PURCHASE_REQUEST", false, true, "SALE-01: purchase request in whole cages, awaiting the Owner.", 1 },
                    { 3902, "ACCEPTED", null, "Accepted", "LEGACY_CAGE_PURCHASE_REQUEST", false, false, "SALE-03: the Owner accepted a cage quantity and pickup date.", 2 },
                    { 3903, "DECLINED", null, "Declined", "LEGACY_CAGE_PURCHASE_REQUEST", true, false, "SALE-03: the Owner declined with a reason.", 3 },
                    { 3904, "CANCELLED", null, "Cancelled", "LEGACY_CAGE_PURCHASE_REQUEST", true, false, "SALE-02: the trader cancelled their own request.", 4 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30077, "Cooperative Owner", 3901, "Positive accepted cage count no larger than requested; pickup date is today or later.", false, 3902, "Action", "SALE-03" },
                    { 30078, "Cooperative Owner", 3901, "The Owner enters a reason.", true, 3903, "Action", "SALE-03" },
                    { 30079, "Trader", 3901, "Only the requesting customer may cancel.", false, 3904, "Action", "SALE-02" },
                    { 30080, "Trader", 3902, "The request is not fulfilled; only the requesting customer may cancel.", false, 3904, "Action", "SALE-02" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequest_Code",
                table: "PurchaseRequest",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequest_CustomerId_ClientOperationId",
                table: "PurchaseRequest",
                columns: new[] { "CustomerId", "ClientOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequest_CustomerId_CreatedAt",
                table: "PurchaseRequest",
                columns: new[] { "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequest_ProductId",
                table: "PurchaseRequest",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequest_RespondedByAccountId",
                table: "PurchaseRequest",
                column: "RespondedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequest_StatusId_CreatedAt",
                table: "PurchaseRequest",
                columns: new[] { "StatusId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseRequest");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 77);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 78);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 79);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 80);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3901);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3902);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3903);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3904);
        }
    }
}
