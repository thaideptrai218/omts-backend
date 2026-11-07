using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaterialUsages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaterialUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsageDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RepairReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialUsages", x => x.Id);
                    table.CheckConstraint("CK_MaterialUsage_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_MaterialUsages_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialUsages_FarmingInputItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "FarmingInputItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialUsages_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialUsages_ItemId",
                table: "MaterialUsages",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialUsages_RaftId_UsageDate",
                table: "MaterialUsages",
                columns: new[] { "RaftId", "UsageDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialUsages_RecordedByAccountId",
                table: "MaterialUsages",
                column: "RecordedByAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaterialUsages");
        }
    }
}
