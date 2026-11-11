using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The cages of a harvest lot are counted by the Owner or Zone Manager and may be a part of a cage (0.5): CageCount becomes decimal(9,2), keeping the whole cages already recorded. The
    /// check "CageCount &gt; 0" depends on the column, so it is dropped and created again around the change. A lot also keeps the reason given when the cycle's harvest went over its target yield.
    /// </summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261111120000_HarvestLotFractionalCagesAndOverPlanReason")]
    public partial class HarvestLotFractionalCagesAndOverPlanReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_HarvestLot_CageCount", table: "HarvestLots");

            migrationBuilder.AlterColumn<decimal>(
                name: "CageCount",
                table: "HarvestLots",
                type: "decimal(9,2)",
                precision: 9,
                scale: 2,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<string>(
                name: "OverPlanReason",
                table: "HarvestLots",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddCheckConstraint(name: "CK_HarvestLot_CageCount", table: "HarvestLots", sql: "[CageCount] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_HarvestLot_CageCount", table: "HarvestLots");

            migrationBuilder.DropColumn(name: "OverPlanReason", table: "HarvestLots");

            // A part of a cage is rounded to the nearest whole cage, and at least one, as the old check needs.
            migrationBuilder.Sql("UPDATE HarvestLots SET CageCount = CASE WHEN ROUND(CageCount, 0) < 1 THEN 1 ELSE ROUND(CageCount, 0) END");

            migrationBuilder.AlterColumn<int>(
                name: "CageCount",
                table: "HarvestLots",
                type: "int",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(9,2)",
                oldPrecision: 9,
                oldScale: 2);

            migrationBuilder.AddCheckConstraint(name: "CK_HarvestLot_CageCount", table: "HarvestLots", sql: "[CageCount] > 0");
        }
    }
}
