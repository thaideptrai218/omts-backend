using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductUnitSalePrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SalePrice",
                table: "ProductUnits",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "UnitCode",
                table: "ProductPriceChanges",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "UnitId",
                table: "ProductPriceChanges",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "UnitName",
                table: "ProductPriceChanges",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            // The base unit's price is the list price of its product; an extra unit starts at factor x that price (whole VND, at least 1).
            migrationBuilder.Sql(
                """
                UPDATE u SET u.SalePrice = CASE WHEN u.IsBase = 1 THEN p.UnitPrice
                                                ELSE CASE WHEN ROUND(u.Factor * p.UnitPrice, 0) < 1 THEN 1 ELSE ROUND(u.Factor * p.UnitPrice, 0) END END
                FROM ProductUnits u JOIN Products p ON p.Id = u.ProductId;
                """);

            // The history rows that exist are the prices per kg: they belong to the base unit of their product.
            migrationBuilder.Sql(
                """
                UPDATE c SET c.UnitId = b.UnitId, c.UnitCode = b.Code, c.UnitName = i.Name
                FROM ProductPriceChanges c
                JOIN ProductUnits b ON b.ProductId = c.ProductId AND b.IsBase = 1
                JOIN InputUnits i ON i.Id = b.UnitId;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductUnit_SalePrice_Positive",
                table: "ProductUnits",
                sql: "[SalePrice] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceChanges_ProductId_UnitId",
                table: "ProductPriceChanges",
                columns: new[] { "ProductId", "UnitId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProductUnit_SalePrice_Positive",
                table: "ProductUnits");

            migrationBuilder.DropIndex(
                name: "IX_ProductPriceChanges_ProductId_UnitId",
                table: "ProductPriceChanges");

            migrationBuilder.DropColumn(
                name: "SalePrice",
                table: "ProductUnits");

            migrationBuilder.DropColumn(
                name: "UnitCode",
                table: "ProductPriceChanges");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "ProductPriceChanges");

            migrationBuilder.DropColumn(
                name: "UnitName",
                table: "ProductPriceChanges");
        }
    }
}
