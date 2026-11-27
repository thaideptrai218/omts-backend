using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MergeTraderCagesIntoHarvestLotCages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand edit: the trader's cages are moved into HarvestLotCages instead of being dropped with their table, and the old unique indexes go first
            // because a trader cage may have the same number as a cooperative cage of the lot.
            migrationBuilder.DropIndex(
                name: "IX_HarvestLotCages_ClientOperationId",
                table: "HarvestLotCages");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLotCages_LotId_Number",
                table: "HarvestLotCages");

            // Hand edit: the existing rows are the cooperative's cages, so they take Cooperative (the scaffold default was an empty string).
            migrationBuilder.AddColumn<string>(
                name: "Side",
                table: "HarvestLotCages",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Cooperative");

            // Hand edit: copy the trader's cages, keeping their ids, as Trader rows (no product).
            migrationBuilder.Sql(
                @"INSERT INTO HarvestLotCages (Id, LotId, Side, Number, ProductId, Kg, WeighedAt, WeighedByAccountId, ClientOperationId)
                  SELECT Id, LotId, 'Trader', Number, NULL, Kg, WeighedAt, WeighedByAccountId, ClientOperationId
                  FROM HarvestLotTraderCages");

            migrationBuilder.DropTable(
                name: "HarvestLotTraderCages");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotCages_LotId_Side_Number",
                table: "HarvestLotCages",
                columns: new[] { "LotId", "Side", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotCages_Side_ClientOperationId",
                table: "HarvestLotCages",
                columns: new[] { "Side", "ClientOperationId" },
                unique: true,
                filter: "[ClientOperationId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_HarvestLotCage_TraderProduct",
                table: "HarvestLotCages",
                sql: "[Side] <> 'Trader' OR [ProductId] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Hand edit: the table is created first so the trader rows can be moved back before the Side column is dropped.
            migrationBuilder.CreateTable(
                name: "HarvestLotTraderCages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    WeighedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WeighedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestLotTraderCages", x => x.Id);
                    table.CheckConstraint("CK_HarvestLotTraderCage_Kg", "[Kg] > 0");
                    table.CheckConstraint("CK_HarvestLotTraderCage_Number", "[Number] >= 1");
                    table.ForeignKey(
                        name: "FK_HarvestLotTraderCages_Accounts_WeighedByAccountId",
                        column: x => x.WeighedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestLotTraderCages_HarvestLots_LotId",
                        column: x => x.LotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotTraderCages_ClientOperationId",
                table: "HarvestLotTraderCages",
                column: "ClientOperationId",
                unique: true,
                filter: "[ClientOperationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotTraderCages_LotId_Number",
                table: "HarvestLotTraderCages",
                columns: new[] { "LotId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotTraderCages_WeighedByAccountId",
                table: "HarvestLotTraderCages",
                column: "WeighedByAccountId");

            // Hand edit: move the Trader rows back, keeping their ids, and remove them from HarvestLotCages.
            migrationBuilder.Sql(
                @"INSERT INTO HarvestLotTraderCages (Id, LotId, Number, Kg, WeighedAt, WeighedByAccountId, ClientOperationId)
                  SELECT Id, LotId, Number, Kg, WeighedAt, WeighedByAccountId, ClientOperationId
                  FROM HarvestLotCages
                  WHERE Side = 'Trader'");

            migrationBuilder.Sql("DELETE FROM HarvestLotCages WHERE Side = 'Trader'");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLotCages_LotId_Side_Number",
                table: "HarvestLotCages");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLotCages_Side_ClientOperationId",
                table: "HarvestLotCages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_HarvestLotCage_TraderProduct",
                table: "HarvestLotCages");

            migrationBuilder.DropColumn(
                name: "Side",
                table: "HarvestLotCages");

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
        }
    }
}
