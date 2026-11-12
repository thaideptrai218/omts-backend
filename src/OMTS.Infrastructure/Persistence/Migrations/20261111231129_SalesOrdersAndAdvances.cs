using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesOrdersAndAdvances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SalesOrderId",
                table: "CustomerPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OrderedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PickupAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PickupDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PurchaseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReceivedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    DiscountDecidedByOwner = table.Column<bool>(type: "bit", nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedManually = table.Column<bool>(type: "bit", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                    table.CheckConstraint("CK_SalesOrder_Amounts", "[DiscountAmount] >= 0 AND [Subtotal] >= 0 AND [Total] >= 0 AND [DiscountAmount] <= [Subtotal] AND [Total] = [Subtotal] - [DiscountAmount]");
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_CancelledByAccountId",
                        column: x => x.CancelledByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_CompletedByAccountId",
                        column: x => x.CompletedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_CreatedByAccountId",
                        column: x => x.CreatedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_ReceivedByAccountId",
                        column: x => x.ReceivedByAccountId,
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
                        name: "FK_SalesOrders_PurchaseRequests_PurchaseRequestId",
                        column: x => x.PurchaseRequestId,
                        principalTable: "PurchaseRequests",
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
                name: "SalesOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ListPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderLines", x => x.Id);
                    table.CheckConstraint("CK_SalesOrderLine_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [Amount] >= 0");
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_SalesOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 3205, "ORDERED", null, "Ordered", "PURCHASE_REQUEST", true, false, "The Cooperative Owner made an order (DH) from the request; the order carries on from here.", 5 },
                    { 4101, "DRAFT", null, "Draft", "SALES_ORDER", false, true, "The order is agreed with the buyer; no invoice has been made from it yet. Its advance is reserved for it.", 1 },
                    { 4102, "DELIVERING", null, "Delivering", "SALES_ORDER", false, false, "At least one invoice was made from the order; goods are still to be delivered. Its advance is reserved for it.", 2 },
                    { 4103, "COMPLETED", null, "Completed", "SALES_ORDER", false, false, "Finished by hand or fully delivered; what is left of its advance is general credit of the customer. A cancelled invoice reopens it.", 3 },
                    { 4104, "CANCELLED", null, "Cancelled", "SALES_ORDER", true, false, "Cancelled with a reason; what is left of its advance is general credit of the customer.", 4 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30084, "System", 4101, "The first invoice of the order is saved with goods still to deliver.", false, 4102, "Action", "SALES-ORDER" },
                    { 30085, "System; Cooperative Owner", 4101, "An invoice covers every line of the order, or the Owner finishes the order.", false, 4103, "Action", "SALES-ORDER" },
                    { 30086, "Cooperative Owner; Zone Manager; System", 4102, "The Owner or a Zone Manager of the zone finishes the order, or every line is delivered.", false, 4103, "Action", "SALES-ORDER" },
                    { 30087, "Cooperative Owner", 4101, "A reason is given.", true, 4104, "Action", "SALES-ORDER" },
                    { 30088, "Cooperative Owner", 4102, "A reason is given and no invoice of the order is left uncancelled; otherwise the order is finished instead.", true, 4104, "Action", "SALES-ORDER" },
                    { 30089, "System", 4103, "An invoice of the finished order is cancelled: the order is open again.", false, 4102, "Action", "SALES-ORDER" },
                    { 30090, "Cooperative Owner", 3201, "The Owner saves an order made from the request.", false, 3205, "Action", "SALES-ORDER" },
                    { 30091, "Cooperative Owner", 3202, "The request is not Fulfilled and has no order yet; the Owner saves an order made from it.", false, 3205, "Action", "SALES-ORDER" }
                });

            // The numbers of the orders (DH000001…) start at 1; the allocator would create the row on first use, this only makes it visible from the start.
            migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM DocumentSequences WHERE [Key] = N'DH') INSERT INTO DocumentSequences ([Key], LastValue) VALUES (N'DH', 0);");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_SalesOrderId",
                table: "CustomerPayments",
                column: "SalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_OrderId_LineNumber",
                table: "SalesOrderLines",
                columns: new[] { "OrderId", "LineNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_ProductId",
                table: "SalesOrderLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CancelledByAccountId",
                table: "SalesOrders",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_Code",
                table: "SalesOrders",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CompletedByAccountId",
                table: "SalesOrders",
                column: "CompletedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CreatedByAccountId",
                table: "SalesOrders",
                column: "CreatedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CustomerId",
                table: "SalesOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_DeduplicationKey",
                table: "SalesOrders",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_PickupDate",
                table: "SalesOrders",
                column: "PickupDate");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_PurchaseRequestId",
                table: "SalesOrders",
                column: "PurchaseRequestId",
                unique: true,
                filter: "[PurchaseRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_ReceivedByAccountId",
                table: "SalesOrders",
                column: "ReceivedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_StatusId_OrderDate",
                table: "SalesOrders",
                columns: new[] { "StatusId", "OrderDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_ZoneId",
                table: "SalesOrders",
                column: "ZoneId");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_SalesOrders_SalesOrderId",
                table: "CustomerPayments",
                column: "SalesOrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The orders go with the tables below: a request made into an order waits for its answer again.
            migrationBuilder.Sql("UPDATE PurchaseRequests SET StatusId = 3201 WHERE StatusId = 3205;");
            migrationBuilder.Sql("DELETE FROM DocumentSequences WHERE [Key] = N'DH';");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_SalesOrders_SalesOrderId",
                table: "CustomerPayments");

            migrationBuilder.DropTable(
                name: "SalesOrderLines");

            migrationBuilder.DropTable(
                name: "SalesOrders");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_SalesOrderId",
                table: "CustomerPayments");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30084);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30085);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30086);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30087);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30088);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30089);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30090);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30091);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3205);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4101);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4102);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4103);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4104);

            migrationBuilder.DropColumn(
                name: "SalesOrderId",
                table: "CustomerPayments");
        }
    }
}
