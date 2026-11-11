using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HarvestLotPurchaseRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseRequestId",
                table: "HarvestLots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_HarvestCompletedByAccountId",
                table: "PurchaseRequests",
                column: "HarvestCompletedByAccountId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseRequest_HarvestCompleted",
                table: "PurchaseRequests",
                sql: "([HarvestCompletedAt] IS NULL AND [HarvestCompletedByAccountId] IS NULL) OR ([HarvestCompletedAt] IS NOT NULL AND [HarvestCompletedByAccountId] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_PurchaseRequestId",
                table: "HarvestLots",
                column: "PurchaseRequestId");

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

            migrationBuilder.DropIndex(
                name: "IX_PurchaseRequests_HarvestCompletedByAccountId",
                table: "PurchaseRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseRequest_HarvestCompleted",
                table: "PurchaseRequests");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLots_PurchaseRequestId",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "HarvestCompletedAt",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "HarvestCompletedByAccountId",
                table: "PurchaseRequests");

            migrationBuilder.DropColumn(
                name: "PurchaseRequestId",
                table: "HarvestLots");
        }
    }
}
