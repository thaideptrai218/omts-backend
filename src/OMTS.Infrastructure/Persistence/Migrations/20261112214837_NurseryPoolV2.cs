using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NurseryPoolV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CloseLossPieces",
                table: "NurseryBatches",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseReason",
                table: "NurseryBatches",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClosedByAccountId",
                table: "NurseryBatches",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ClosedOn",
                table: "NurseryBatches",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "CultivationCycles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30090,
                column: "GuardCondition",
                value: "The survivors are not above the pieces put in; no share and no cycle is created (v2, 13/11/2026).");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4002,
                columns: new[] { "IsFinal", "Meaning" },
                values: new object[] { false, "The survivors are recorded (Đã ươm xong): the batch is a pool that cycles seed from; the survivors can still be corrected, not below what is seeded. It is closed once the rafts are seeded." });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[] { 4004, "CLOSED", null, "Closed", "NURSERY_BATCH", true, false, "Closed with pieces left in the pool: the remainder is written off as an estimated loss; nothing is seeded from it any more.", 4 });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 30092, "Cooperative Owner; Zone Manager", 4002, "The batch is Completed and pieces are left in the pool; they are written off as an estimated loss.", false, 4004, "Action", "NURSERY-05" });

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatches_ClosedByAccountId",
                table: "NurseryBatches",
                column: "ClosedByAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_NurseryBatches_Accounts_ClosedByAccountId",
                table: "NurseryBatches",
                column: "ClosedByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NurseryBatches_Accounts_ClosedByAccountId",
                table: "NurseryBatches");

            migrationBuilder.DropIndex(
                name: "IX_NurseryBatches_ClosedByAccountId",
                table: "NurseryBatches");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30092);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4004);

            migrationBuilder.DropColumn(
                name: "CloseLossPieces",
                table: "NurseryBatches");

            migrationBuilder.DropColumn(
                name: "CloseReason",
                table: "NurseryBatches");

            migrationBuilder.DropColumn(
                name: "ClosedByAccountId",
                table: "NurseryBatches");

            migrationBuilder.DropColumn(
                name: "ClosedOn",
                table: "NurseryBatches");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "CultivationCycles");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30090,
                column: "GuardCondition",
                value: "The survivors are not above the pieces put in, and the shares of the rafts of the zone, none with an Open cycle, add up to the survivors exactly.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4002,
                columns: new[] { "IsFinal", "Meaning" },
                values: new object[] { true, "The survivors are recorded and each raft has its cycle; the survivors and shares can still be corrected before the seed is hung." });
        }
    }
}
