using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HarvestLotWeighingAndLotCloseLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WeighedAt",
                table: "HarvestLots",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WeighedByAccountId",
                table: "HarvestLots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeighedKg",
                table: "HarvestLots",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WeighedOn",
                table: "HarvestLots",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeighedKg",
                table: "HarvestLotProductLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 152,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner; Zone Manager; System", "A sales return puts oysters back, or the invoice that closed the lot is cancelled or loses its tick (the system); a Zone Manager or the Owner reopens a lot closed Unsold, Spoiled or by the time limit, not by an invoice, with a reason. The kg taken off the stock at the close come back.", "CLAIM-03, SALE-07, SALE-08, HARVEST-REOPEN" });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30118, "System", 4402, "The only invoice of the order that is not cancelled is cancelled: no invoice is left, so the order is a draft again.", false, 4401, "Action", "SALES-ORDER" },
                    { 30119, "System", 4403, "The only invoice of the finished order that is not cancelled is cancelled: no invoice is left, so the order is a draft again.", false, 4401, "Action", "SALES-ORDER" }
                });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2901,
                columns: new[] { "IsInitial", "Meaning" },
                values: new object[] { false, "For sale: it can be chosen on a sales invoice. Weighed; it can be weighed again." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2903,
                column: "Meaning",
                value: "No longer for sale; its close reason is kept and what was left of its products is taken off the stock. The system can reopen it; a lot closed by hand or by the time limit is also reopened by hand.");

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[] { 2904, "AWAITING_WEIGHING", null, "Awaiting weighing", "HARVEST_LOT", false, true, "Recorded with its estimated kg (Chờ cân); not for sale and in no stock until it is weighed. It can be corrected or removed.", 4 });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 30117, "Cooperative Owner; Zone Manager", 2904, "The lot is weighed: each product line has its weighed kg, which go into the stock of the products.", false, 2901, "Action", "HARVEST-WEIGH" });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_WeighedByAccountId",
                table: "HarvestLots",
                column: "WeighedByAccountId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_HarvestLot_WeighedKg",
                table: "HarvestLots",
                sql: "[WeighedKg] IS NULL OR [WeighedKg] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_HarvestLotProductLine_WeighedKg",
                table: "HarvestLotProductLines",
                sql: "[WeighedKg] IS NULL OR [WeighedKg] > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestLots_Accounts_WeighedByAccountId",
                table: "HarvestLots",
                column: "WeighedByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Every lot there is was weighed in effect: its estimate is its weighed kg, at the time and day of the harvest, so the figures, the stock and the days before automatic close read as before.
            migrationBuilder.Sql(@"
UPDATE HarvestLots SET WeighedKg = EstimatedKg, WeighedAt = HarvestedAt, WeighedOn = HarvestDate WHERE WeighedKg IS NULL;
UPDATE HarvestLotProductLines SET WeighedKg = Kg WHERE WeighedKg IS NULL;");

            // A lot already Closed holds no stock from now on: what is left of each of its products on the ledger is taken off, at the end of the day it was closed.
            migrationBuilder.Sql(@"
INSERT INTO ProductStockMovements (Id, ProductId, HarvestLotId, LotCode, Kind, DocumentId, DocumentCode, OccurredAt, QuantityKg, RecordedAt)
SELECT NEWID(), m.ProductId, l.Id, l.LotCode, 'LotClose', l.Id, l.LotCode,
       COALESCE(TODATETIMEOFFSET(DATEADD(second, -1, CAST(DATEADD(day, 1, l.CloseDate) AS datetime2)), '+07:00'), SYSDATETIMEOFFSET()),
       -SUM(m.QuantityKg), SYSDATETIMEOFFSET()
FROM ProductStockMovements m
JOIN HarvestLots l ON l.Id = m.HarvestLotId
WHERE l.StatusId = 2903
GROUP BY m.ProductId, l.Id, l.LotCode, l.CloseDate
HAVING SUM(m.QuantityKg) <> 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The close rows go with the kind, and a lot that waits for its weighing is for sale again, as before the status existed.
            migrationBuilder.Sql(@"
DELETE FROM ProductStockMovements WHERE ReversesMovementId IN (SELECT Id FROM ProductStockMovements WHERE Kind = 'LotClose');
DELETE FROM ProductStockMovements WHERE Kind = 'LotClose';
UPDATE HarvestLots SET StatusId = 2901 WHERE StatusId = 2904;");

            migrationBuilder.DropForeignKey(
                name: "FK_HarvestLots_Accounts_WeighedByAccountId",
                table: "HarvestLots");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLots_WeighedByAccountId",
                table: "HarvestLots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_HarvestLot_WeighedKg",
                table: "HarvestLots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_HarvestLotProductLine_WeighedKg",
                table: "HarvestLotProductLines");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30117);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30118);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30119);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2904);

            migrationBuilder.DropColumn(
                name: "WeighedAt",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "WeighedByAccountId",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "WeighedKg",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "WeighedOn",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "WeighedKg",
                table: "HarvestLotProductLines");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 152,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "System", "A sales return puts oysters back, or the invoice that closed the lot is cancelled or loses its tick.", "CLAIM-03, SALE-07, SALE-08" });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2901,
                columns: new[] { "IsInitial", "Meaning" },
                values: new object[] { true, "For sale: it can be chosen on a sales invoice." });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2903,
                column: "Meaning",
                value: "No longer for sale; its close reason is kept. The system can reopen it.");
        }
    }
}
