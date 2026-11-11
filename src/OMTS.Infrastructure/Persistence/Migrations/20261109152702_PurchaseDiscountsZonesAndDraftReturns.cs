using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseDiscountsZonesAndDraftReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "SupplierPayments",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseReturnId",
                table: "SupplierPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ReceiptId",
                table: "PurchaseReturns",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<decimal>(
                name: "Discount",
                table: "PurchaseReturns",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                table: "PurchaseReturns",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                table: "PurchaseReturns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ReceiptLineId",
                table: "PurchaseReturnLines",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "ItemId",
                table: "PurchaseReturnLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "PurchaseReturnLines",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                table: "PurchaseReturnLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Discount",
                table: "PurchaseReceipts",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                table: "PurchaseReceipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[] { 1903, "DRAFT", null, "Draft", "PURCHASE_RETURN", false, true, "Being prepared; counts nowhere and holds no quantity. Removed, never cancelled.", 3 });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 30077, "Zone Manager (own drafts); Cooperative Owner", 1903, "The return is complete and within the quantity left on each line of its receipt; dated the day it is completed.", false, 1901, "Action", "PURCHASE-02" });

            // Backfilled before the foreign keys: the supplier and the items come from the receipts the existing returns were made from.
            migrationBuilder.Sql(@"
UPDATE r SET r.SupplierId = pr.SupplierId
FROM PurchaseReturns r JOIN PurchaseReceipts pr ON pr.Id = r.ReceiptId;

UPDATE rl SET rl.ItemId = l.ItemId, rl.Unit = l.Unit, rl.UnitPrice = COALESCE(l.UnitPrice, 0)
FROM PurchaseReturnLines rl JOIN PurchaseReceiptLines l ON l.Id = rl.ReceiptLineId;

-- A receipt gets a zone only when every raft it names is in that one zone.
UPDATE pr SET pr.ZoneId = z.ZoneId
FROM PurchaseReceipts pr
JOIN (
    SELECT l.ReceiptId, MIN(CONVERT(char(36), ra.ZoneId)) AS ZoneId
    FROM PurchaseReceiptLines l JOIN Rafts ra ON ra.Id = l.RaftId
    GROUP BY l.ReceiptId
    HAVING COUNT(DISTINCT ra.ZoneId) = 1
) z ON z.ReceiptId = pr.Id;

UPDATE r SET r.ZoneId = pr.ZoneId
FROM PurchaseReturns r JOIN PurchaseReceipts pr ON pr.Id = r.ReceiptId;
");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_PurchaseReturnId",
                table: "SupplierPayments",
                column: "PurchaseReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_SupplierId_StatusId",
                table: "PurchaseReturns",
                columns: new[] { "SupplierId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_ZoneId",
                table: "PurchaseReturns",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_ItemId",
                table: "PurchaseReturnLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_ZoneId",
                table: "PurchaseReceipts",
                column: "ZoneId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReceipts_FarmingZones_ZoneId",
                table: "PurchaseReceipts",
                column: "ZoneId",
                principalTable: "FarmingZones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturnLines_FarmingInputItems_ItemId",
                table: "PurchaseReturnLines",
                column: "ItemId",
                principalTable: "FarmingInputItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturns_FarmingZones_ZoneId",
                table: "PurchaseReturns",
                column: "ZoneId",
                principalTable: "FarmingZones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturns_Suppliers_SupplierId",
                table: "PurchaseReturns",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierPayments_PurchaseReturns_PurchaseReturnId",
                table: "SupplierPayments",
                column: "PurchaseReturnId",
                principalTable: "PurchaseReturns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The older schema has no drafts and no return without a receipt: such returns, their lines and the refunds
            // recorded on them are removed, or the draft status and the NOT NULL receipt columns below would fail.
            migrationBuilder.Sql(@"
DELETE FROM SupplierPayments WHERE PurchaseReturnId IN (SELECT Id FROM PurchaseReturns WHERE StatusId = 1903 OR ReceiptId IS NULL);

DELETE rl FROM PurchaseReturnLines rl JOIN PurchaseReturns r ON r.Id = rl.ReturnId
WHERE r.StatusId = 1903 OR r.ReceiptId IS NULL OR rl.ReceiptLineId IS NULL;

DELETE FROM PurchaseReturns WHERE StatusId = 1903 OR ReceiptId IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReceipts_FarmingZones_ZoneId",
                table: "PurchaseReceipts");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturnLines_FarmingInputItems_ItemId",
                table: "PurchaseReturnLines");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturns_FarmingZones_ZoneId",
                table: "PurchaseReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturns_Suppliers_SupplierId",
                table: "PurchaseReturns");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierPayments_PurchaseReturns_PurchaseReturnId",
                table: "SupplierPayments");

            migrationBuilder.DropIndex(
                name: "IX_SupplierPayments_PurchaseReturnId",
                table: "SupplierPayments");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_SupplierId_StatusId",
                table: "PurchaseReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_ZoneId",
                table: "PurchaseReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturnLines_ItemId",
                table: "PurchaseReturnLines");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceipts_ZoneId",
                table: "PurchaseReceipts");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30077);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1903);

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "PurchaseReturnId",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "Discount",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "ItemId",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "UnitPrice",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "Discount",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "PurchaseReceipts");

            migrationBuilder.AlterColumn<Guid>(
                name: "ReceiptId",
                table: "PurchaseReturns",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ReceiptLineId",
                table: "PurchaseReturnLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
