using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CultivationCycleProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                table: "CultivationCycles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CultivationCycles_ProductId",
                table: "CultivationCycles",
                column: "ProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_CultivationCycles_Products_ProductId",
                table: "CultivationCycles",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // An Open cycle takes the product of the biggest product line of its latest harvest lot; a cycle with no such lot, and every Closed cycle, stays without a product
            // (the product is required when a cycle starts, not by the column).
            migrationBuilder.Sql(@"
UPDATE cy SET ProductId = cp.ProductId
FROM CultivationCycles cy
CROSS APPLY (
    SELECT TOP 1 pl.ProductId
    FROM HarvestLotProductLines pl
    JOIN HarvestLots lt ON lt.Id = pl.LotId
    WHERE lt.CycleId = cy.Id
    ORDER BY lt.HarvestedAt DESC, pl.Kg DESC
) cp
WHERE cy.StatusId = 601 AND cy.ProductId IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CultivationCycles_Products_ProductId",
                table: "CultivationCycles");

            migrationBuilder.DropIndex(
                name: "IX_CultivationCycles_ProductId",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "CultivationCycles");
        }
    }
}
