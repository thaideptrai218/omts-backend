using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The product a cultivation cycle will yield and sell (owner decision of 13/11/2026): a harvest lot can only be recorded for a purchase request of that product. Nullable: cycles started before keep none
    /// and are not checked. Down drops the column.
    /// </summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261113100000_CultivationCycleProduct")]
    public partial class CultivationCycleProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "ProductId", table: "CultivationCycles", type: "uniqueidentifier", nullable: true);

            migrationBuilder.CreateIndex(name: "IX_CultivationCycles_ProductId", table: "CultivationCycles", column: "ProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_CultivationCycles_Products_ProductId", table: "CultivationCycles", column: "ProductId", principalTable: "Products", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_CultivationCycles_Products_ProductId", table: "CultivationCycles");

            migrationBuilder.DropIndex(name: "IX_CultivationCycles_ProductId", table: "CultivationCycles");

            migrationBuilder.DropColumn(name: "ProductId", table: "CultivationCycles");
        }
    }
}
