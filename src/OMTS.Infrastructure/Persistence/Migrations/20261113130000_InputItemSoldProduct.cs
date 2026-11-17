using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The product a seed input item sells as (owner decision of 13/11/2026): a cycle started from a nursery batch of the seed farms this product, so the cycle no longer asks for one. Nullable: only Seed items use it
    /// and the Owner sets it once in the catalogue. Down drops the column.
    /// </summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261113130000_InputItemSoldProduct")]
    public partial class InputItemSoldProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "ProductId", table: "FarmingInputItems", type: "uniqueidentifier", nullable: true);

            migrationBuilder.CreateIndex(name: "IX_FarmingInputItems_ProductId", table: "FarmingInputItems", column: "ProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_FarmingInputItems_Products_ProductId", table: "FarmingInputItems", column: "ProductId", principalTable: "Products", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_FarmingInputItems_Products_ProductId", table: "FarmingInputItems");

            migrationBuilder.DropIndex(name: "IX_FarmingInputItems_ProductId", table: "FarmingInputItems");

            migrationBuilder.DropColumn(name: "ProductId", table: "FarmingInputItems");
        }
    }
}
