using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseReturnsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseReturn_DebtReductionAmount_Thousands",
                table: "PurchaseReturns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseReturn_RefundAmount_Thousands",
                table: "PurchaseReturns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseReturnLine_ReturnValue_Thousands",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "DebtReductionAmount",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "EvidenceUrls",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "RefundAmount",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "RefundMethod",
                table: "PurchaseReturns");

            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "PurchaseReturns",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "PurchaseReturns",
                type: "datetimeoffset",
                nullable: true);

            // Every return recorded so far stands: Recorded.
            migrationBuilder.AddColumn<int>(
                name: "StatusId",
                table: "PurchaseReturns",
                type: "int",
                nullable: false,
                defaultValue: 1901);

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 1901, "RECORDED", null, "Recorded", "PURCHASE_RETURN", false, true, "Lowers the supplier debt and the material cost of the raft; cannot be edited.", 1 },
                    { 1902, "CANCELLED", null, "Cancelled", "PURCHASE_RETURN", true, false, "Recorded by mistake; no longer counts.", 2 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 74, "Cooperative Owner", 1901, "A return recorded by mistake; it no longer counts in the supplier debt, the material cost of a raft or the quantity returned.", true, 1902, "Action", "PURCHASE-02" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_StatusId",
                table: "PurchaseReturns",
                column: "StatusId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturns_Statuses_StatusId",
                table: "PurchaseReturns",
                column: "StatusId",
                principalTable: "Statuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy: the old model has no Cancelled return and only whole thousands.
            migrationBuilder.Sql(
                """
                DELETE FROM PurchaseReturnLines WHERE ReturnId IN (SELECT Id FROM PurchaseReturns WHERE StatusId = 1902);
                DELETE FROM PurchaseReturns WHERE StatusId = 1902;
                UPDATE PurchaseReturnLines SET ReturnValue = ROUND(ReturnValue, -3) WHERE ReturnValue % 1000 <> 0;
                DELETE FROM DocumentSequences WHERE [Key] = N'TH';
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturns_Statuses_StatusId",
                table: "PurchaseReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_StatusId",
                table: "PurchaseReturns");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 74);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1901);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1902);

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "StatusId",
                table: "PurchaseReturns");

            migrationBuilder.AddColumn<decimal>(
                name: "DebtReductionAmount",
                table: "PurchaseReturns",
                type: "decimal(18,0)",
                precision: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceUrls",
                table: "PurchaseReturns",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<decimal>(
                name: "RefundAmount",
                table: "PurchaseReturns",
                type: "decimal(18,0)",
                precision: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "RefundMethod",
                table: "PurchaseReturns",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // The old model splits each return's value into a refund and a debt reduction: all of it lowered the debt.
            migrationBuilder.Sql(
                """
                UPDATE r SET DebtReductionAmount = ISNULL((SELECT SUM(l.ReturnValue) FROM PurchaseReturnLines l WHERE l.ReturnId = r.Id), 0)
                FROM PurchaseReturns r;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseReturn_DebtReductionAmount_Thousands",
                table: "PurchaseReturns",
                sql: "[DebtReductionAmount] % 1000 = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseReturn_RefundAmount_Thousands",
                table: "PurchaseReturns",
                sql: "[RefundAmount] % 1000 = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseReturnLine_ReturnValue_Thousands",
                table: "PurchaseReturnLines",
                sql: "[ReturnValue] % 1000 = 0");
        }
    }
}
