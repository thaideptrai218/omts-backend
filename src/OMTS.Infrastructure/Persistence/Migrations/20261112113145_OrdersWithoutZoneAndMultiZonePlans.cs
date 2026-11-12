using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrdersWithoutZoneAndMultiZonePlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A line of a plan not yet over has no product of its own any more: it takes the product its raft's Open cultivation cycle farms (the product of the cycle's latest lot with product lines).
            // The zone of a line is the zone of its raft, so the plan's own zone needs no copy; lines of finished plans keep what they have.
            migrationBuilder.Sql(@"
UPDATE hl SET ExpectedProductId = cp.ProductId
FROM HarvestPlanLines hl
JOIN HarvestPlans hp ON hp.Id = hl.PlanId AND hp.StatusId IN (4201, 4202, 4203)
CROSS APPLY (
    SELECT TOP 1 pl.ProductId
    FROM HarvestLotProductLines pl
    JOIN HarvestLots lt ON lt.Id = pl.LotId
    JOIN CultivationCycles cy ON cy.Id = lt.CycleId AND cy.StatusId = 601
    WHERE lt.RaftId = hl.RaftId
    ORDER BY lt.HarvestedAt DESC, pl.Kg DESC
) cp
WHERE hl.ExpectedProductId IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_HarvestPlans_FarmingZones_ZoneId",
                table: "HarvestPlans");

            migrationBuilder.DropIndex(
                name: "IX_HarvestPlans_ZoneId",
                table: "HarvestPlans");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "HarvestPlans");

            migrationBuilder.AlterColumn<Guid>(
                name: "ZoneId",
                table: "SalesOrders",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Every order and every plan had a zone before: an order that was saved without one takes the zone of its first invoice, else of its first plan, else the first zone by code; a plan takes the
            // zone of the raft of its first line, else the first zone by code. The product of a line stays as it is.
            migrationBuilder.Sql(@"
UPDATE ord SET ZoneId = COALESCE(
    (SELECT TOP 1 inv.ZoneId FROM SalesInvoices inv WHERE inv.SalesOrderId = ord.Id ORDER BY inv.InvoicedAt, inv.Code),
    (SELECT TOP 1 rf.ZoneId FROM SalesOrderHarvestPlans lk
        JOIN HarvestPlanLines hl ON hl.PlanId = lk.PlanId
        JOIN Rafts rf ON rf.Id = hl.RaftId
        WHERE lk.OrderId = ord.Id ORDER BY hl.LineNumber),
    (SELECT TOP 1 zn.Id FROM FarmingZones zn ORDER BY zn.Code))
FROM SalesOrders ord
WHERE ord.ZoneId IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "ZoneId",
                table: "SalesOrders",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                table: "HarvestPlans",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE hp SET ZoneId = COALESCE(
    (SELECT TOP 1 rf.ZoneId FROM HarvestPlanLines hl JOIN Rafts rf ON rf.Id = hl.RaftId WHERE hl.PlanId = hp.Id ORDER BY hl.LineNumber),
    (SELECT TOP 1 zn.Id FROM FarmingZones zn ORDER BY zn.Code))
FROM HarvestPlans hp;");

            migrationBuilder.AlterColumn<Guid>(
                name: "ZoneId",
                table: "HarvestPlans",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_ZoneId",
                table: "HarvestPlans",
                column: "ZoneId");

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestPlans_FarmingZones_ZoneId",
                table: "HarvestPlans",
                column: "ZoneId",
                principalTable: "FarmingZones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
