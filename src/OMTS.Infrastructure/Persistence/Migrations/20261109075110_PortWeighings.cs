using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PortWeighings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PortWeighingId",
                table: "SalesInvoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PortWeighings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WeighedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WeighedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfirmedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortWeighings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortWeighings_Accounts_ConfirmedByAccountId",
                        column: x => x.ConfirmedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortWeighings_Accounts_WeighedByAccountId",
                        column: x => x.WeighedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortWeighings_PurchaseRequests_PurchaseRequestId",
                        column: x => x.PurchaseRequestId,
                        principalTable: "PurchaseRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PortWeighingCages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PortWeighingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CageNumber = table.Column<int>(type: "int", nullable: false),
                    HarvestLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortWeighingCages", x => x.Id);
                    table.CheckConstraint("CK_PortWeighingCage_Kg", "[Kg] > 0");
                    table.CheckConstraint("CK_PortWeighingCage_Number", "[CageNumber] >= 1");
                    table.ForeignKey(
                        name: "FK_PortWeighingCages_HarvestLots_HarvestLotId",
                        column: x => x.HarvestLotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortWeighingCages_PortWeighings_PortWeighingId",
                        column: x => x.PortWeighingId,
                        principalTable: "PortWeighings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_PortWeighingId",
                table: "SalesInvoices",
                column: "PortWeighingId",
                unique: true,
                filter: "[PortWeighingId] IS NOT NULL AND [StatusId] = 3501");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SalesInvoice_Weighing_Has_Request",
                table: "SalesInvoices",
                sql: "[PortWeighingId] IS NULL OR [PurchaseRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighingCages_HarvestLotId",
                table: "PortWeighingCages",
                column: "HarvestLotId");

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighingCages_PortWeighingId_CageNumber",
                table: "PortWeighingCages",
                columns: new[] { "PortWeighingId", "CageNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighings_ConfirmedByAccountId",
                table: "PortWeighings",
                column: "ConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighings_PurchaseRequestId_WeighedAt",
                table: "PortWeighings",
                columns: new[] { "PurchaseRequestId", "WeighedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PortWeighings_WeighedByAccountId",
                table: "PortWeighings",
                column: "WeighedByAccountId");

            migrationBuilder.CreateIndex(
                name: "UX_PortWeighing_One_Unconfirmed_Per_Request",
                table: "PortWeighings",
                column: "PurchaseRequestId",
                unique: true,
                filter: "[ConfirmedAt] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoices_PortWeighings_PortWeighingId",
                table: "SalesInvoices",
                column: "PortWeighingId",
                principalTable: "PortWeighings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoices_PortWeighings_PortWeighingId",
                table: "SalesInvoices");

            migrationBuilder.DropTable(
                name: "PortWeighingCages");

            migrationBuilder.DropTable(
                name: "PortWeighings");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_PortWeighingId",
                table: "SalesInvoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SalesInvoice_Weighing_Has_Request",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "PortWeighingId",
                table: "SalesInvoices");
        }
    }
}
