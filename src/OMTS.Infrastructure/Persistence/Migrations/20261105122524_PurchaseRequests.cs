using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    PickupDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    AgreedPickupDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PayLaterAgreed = table.Column<bool>(type: "bit", nullable: true),
                    ResponseNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RespondedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequests", x => x.Id);
                    table.CheckConstraint("CK_PurchaseRequest_Accepted_Has_Response", "[StatusId] <> 3202 OR ([AcceptedKg] IS NOT NULL AND [AgreedPickupDate] IS NOT NULL AND [PayLaterAgreed] IS NOT NULL)");
                    table.CheckConstraint("CK_PurchaseRequest_AcceptedKg_Range", "[AcceptedKg] IS NULL OR ([AcceptedKg] > 0 AND [AcceptedKg] <= [RequestedKg])");
                    table.CheckConstraint("CK_PurchaseRequest_Declined_Has_Note", "[StatusId] <> 3203 OR [ResponseNote] IS NOT NULL");
                    table.CheckConstraint("CK_PurchaseRequest_RequestedKg_Positive", "[RequestedKg] > 0");
                    table.ForeignKey(
                        name: "FK_PurchaseRequests_Accounts_RespondedByAccountId",
                        column: x => x.RespondedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseRequests_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseRequests_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseRequests_Statuses_StatusId",
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
                    { 3201, "AWAITING_RESPONSE", null, "Awaiting response", "PURCHASE_REQUEST", false, true, "Sent by the trader; not answered yet.", 1 },
                    { 3202, "ACCEPTED", null, "Accepted", "PURCHASE_REQUEST", false, false, "Accepted by the Cooperative Owner, possibly for fewer kg; invoices may be linked to it.", 2 },
                    { 3203, "DECLINED", null, "Declined", "PURCHASE_REQUEST", true, false, "Declined by the Cooperative Owner with a note.", 3 },
                    { 3204, "CANCELLED", null, "Cancelled", "PURCHASE_REQUEST", true, false, "Cancelled by the trader; no invoice can be linked to it.", 4 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 180, "Cooperative Owner", 3201, "The accepted kg is above 0 and not more than the requested kg; the pickup date is not before today.", false, 3202, "Action", "SALE-03" },
                    { 181, "Cooperative Owner", 3201, "A note gives the reason.", true, 3203, "Action", "SALE-03" },
                    { 182, "Trader", 3201, "The trader cancels; a reason is optional.", false, 3204, "Action", "SALE-02" },
                    { 183, "Trader", 3202, "The request is not Fulfilled; a reason is optional.", false, 3204, "Action", "SALE-02" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_CustomerId_SentAt",
                table: "PurchaseRequests",
                columns: new[] { "CustomerId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_ProductId",
                table: "PurchaseRequests",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_RespondedByAccountId",
                table: "PurchaseRequests",
                column: "RespondedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_StatusId_SentAt",
                table: "PurchaseRequests",
                columns: new[] { "StatusId", "SentAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseRequests");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 180);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 181);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 182);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 183);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3201);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3202);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3203);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3204);
        }
    }
}
