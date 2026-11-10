using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseReceiptTimeReceiverAndLineDiscount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReceivedAt",
                table: "PurchaseReceipts",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "ReceivedByAccountId",
                table: "PurchaseReceipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Discount",
                table: "PurchaseReceiptLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            // An older receipt was received at the start of its date in Vietnam, by whoever recorded it; its lines had no discount.
            migrationBuilder.Sql(@"
UPDATE PurchaseReceipts
SET ReceivedAt = TODATETIMEOFFSET(CAST(ReceiptDate AS datetime2), '+07:00'),
    ReceivedByAccountId = RecordedByAccountId;
");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 74,
                column: "GuardCondition",
                value: "A return recorded by mistake; it no longer counts in the supplier debt or the quantity returned.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1702,
                column: "Meaning",
                value: "Counts in the supplier debt; each Seed line is a seed lot. May be edited, never its supplier, and every edit is audited.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1901,
                column: "Meaning",
                value: "Lowers the supplier debt. May be edited within the quantity left, never below the money paid back, and every edit is audited.");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_ReceivedByAccountId",
                table: "PurchaseReceipts",
                column: "ReceivedByAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReceipts_Accounts_ReceivedByAccountId",
                table: "PurchaseReceipts",
                column: "ReceivedByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReceipts_Accounts_ReceivedByAccountId",
                table: "PurchaseReceipts");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceipts_ReceivedByAccountId",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "ReceivedAt",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "ReceivedByAccountId",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "Discount",
                table: "PurchaseReceiptLines");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 74,
                column: "GuardCondition",
                value: "A return recorded by mistake; it no longer counts in the supplier debt, the material cost of a raft or the quantity returned.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1702,
                column: "Meaning",
                value: "Cannot be changed; counts in the supplier debt and raft material costs; each Seed line is a seed lot.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1901,
                column: "Meaning",
                value: "Lowers the supplier debt and the material cost of the raft; cannot be edited.");
        }
    }
}
