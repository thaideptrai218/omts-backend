using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HarvestLotTraderWeighing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WeighingAgreedAt",
                table: "HarvestLots",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WeighingAgreedByAccountId",
                table: "HarvestLots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WeighingAgreedFor",
                table: "HarvestLots",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeighingAgreedKg",
                table: "HarvestLots",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HarvestLotTraderCages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Kg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    WeighedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WeighedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
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
                name: "IX_HarvestLots_WeighingAgreedByAccountId",
                table: "HarvestLots",
                column: "WeighingAgreedByAccountId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestLots_Accounts_WeighingAgreedByAccountId",
                table: "HarvestLots",
                column: "WeighingAgreedByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HarvestLots_Accounts_WeighingAgreedByAccountId",
                table: "HarvestLots");

            migrationBuilder.DropTable(
                name: "HarvestLotTraderCages");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLots_WeighingAgreedByAccountId",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "WeighingAgreedAt",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "WeighingAgreedByAccountId",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "WeighingAgreedFor",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "WeighingAgreedKg",
                table: "HarvestLots");
        }
    }
}
