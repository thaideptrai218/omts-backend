using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HarvestLotCages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HarvestLotCages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    WeighedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WeighedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestLotCages", x => x.Id);
                    table.CheckConstraint("CK_HarvestLotCage_Kg", "[Kg] > 0");
                    table.CheckConstraint("CK_HarvestLotCage_Number", "[Number] >= 1");
                    table.ForeignKey(
                        name: "FK_HarvestLotCages_Accounts_WeighedByAccountId",
                        column: x => x.WeighedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestLotCages_HarvestLots_LotId",
                        column: x => x.LotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestLotCages_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotCages_ClientOperationId",
                table: "HarvestLotCages",
                column: "ClientOperationId",
                unique: true,
                filter: "[ClientOperationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotCages_LotId_Number",
                table: "HarvestLotCages",
                columns: new[] { "LotId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotCages_ProductId",
                table: "HarvestLotCages",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotCages_WeighedByAccountId",
                table: "HarvestLotCages",
                column: "WeighedByAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HarvestLotCages");
        }
    }
}
