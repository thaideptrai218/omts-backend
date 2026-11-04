using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PR H1a (SRS 27/10, HARVEST-01, HARVEST-03): harvest lots with their lanes and photos, the HARVEST_LOT status group, and the two harvest settings on the cooperative, which the
    /// existing row takes at the first-setup values 50 and 3 (BR-HARVEST-06). Down drops the lots and settings; stored data is dev and demo only.
    /// </summary>
    public partial class HarvestLots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AverageKgPerCage",
                table: "Cooperatives",
                type: "decimal(9,2)",
                precision: 9,
                scale: 2,
                nullable: false,
                defaultValue: 50m);

            migrationBuilder.AddColumn<int>(
                name: "DaysBeforeAutoClose",
                table: "Cooperatives",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.CreateTable(
                name: "HarvestLots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HarvestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HarvestDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CageCount = table.Column<int>(type: "int", nullable: false),
                    KgPerCage = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    EstimatedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    ReopenedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CloseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LeftoverKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ClosedBySalesInvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WithdrawalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestLots", x => x.Id);
                    table.CheckConstraint("CK_HarvestLot_CageCount", "[CageCount] > 0");
                    table.CheckConstraint("CK_HarvestLot_EstimatedKg", "[EstimatedKg] > 0");
                    table.CheckConstraint("CK_HarvestLot_KgPerCage", "[KgPerCage] > 0");
                    table.CheckConstraint("CK_HarvestLot_LeftoverKg", "[LeftoverKg] IS NULL OR [LeftoverKg] >= 0");
                    table.ForeignKey(
                        name: "FK_HarvestLots_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestLots_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestLots_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestLots_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HarvestLotLanes",
                columns: table => new
                {
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsFinished = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestLotLanes", x => new { x.LotId, x.LaneId });
                    table.ForeignKey(
                        name: "FK_HarvestLotLanes_HarvestLots_LotId",
                        column: x => x.LotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestLotLanes_Lanes_LaneId",
                        column: x => x.LaneId,
                        principalTable: "Lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HarvestLotPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    FileKey = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestLotPhotos", x => x.Id);
                    table.CheckConstraint("CK_HarvestLotPhoto_SortOrder", "[SortOrder] BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_HarvestLotPhotos_HarvestLots_LotId",
                        column: x => x.LotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 2901, "OPEN", null, "Open", "HARVEST_LOT", false, true, "For sale: it can be chosen on a sales invoice.", 1 },
                    { 2902, "REOPENED", null, "Reopened", "HARVEST_LOT", false, false, "Closed earlier and for sale again; the days before automatic close count from the day it was reopened.", 2 },
                    { 2903, "CLOSED", null, "Closed", "HARVEST_LOT", false, false, "No longer for sale; its close reason is kept. The system can reopen it.", 3 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 150, "Cooperative Owner; Zone Manager; System", 2901, "Sold whole lot, closed with the leftover kg, or the time limit reached.", false, 2903, "Action", "HARVEST-02, GFR-05, SALE-05, SALE-07, CLAIM-03" },
                    { 151, "Cooperative Owner; Zone Manager; System", 2902, "Closed again in the same four ways.", false, 2903, "Action", "HARVEST-02, GFR-05, SALE-05, SALE-07, CLAIM-03" },
                    { 152, "System", 2903, "A sales return puts oysters back, or the invoice that closed the lot is cancelled or loses its tick.", false, 2902, "System", "CLAIM-03, SALE-07, SALE-08" }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cooperative_AverageKgPerCage",
                table: "Cooperatives",
                sql: "[AverageKgPerCage] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cooperative_DaysBeforeAutoClose",
                table: "Cooperatives",
                sql: "[DaysBeforeAutoClose] >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotLanes_LaneId",
                table: "HarvestLotLanes",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotPhotos_LotId_SortOrder",
                table: "HarvestLotPhotos",
                columns: new[] { "LotId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_CycleId",
                table: "HarvestLots",
                column: "CycleId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_LotCode",
                table: "HarvestLots",
                column: "LotCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_RaftId_HarvestDate",
                table: "HarvestLots",
                columns: new[] { "RaftId", "HarvestDate" });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_RecordedByAccountId",
                table: "HarvestLots",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_StatusId_HarvestDate",
                table: "HarvestLots",
                columns: new[] { "StatusId", "HarvestDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HarvestLotLanes");

            migrationBuilder.DropTable(
                name: "HarvestLotPhotos");

            migrationBuilder.DropTable(
                name: "HarvestLots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cooperative_AverageKgPerCage",
                table: "Cooperatives");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cooperative_DaysBeforeAutoClose",
                table: "Cooperatives");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 150);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 151);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 152);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2901);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2902);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2903);

            migrationBuilder.DropColumn(
                name: "AverageKgPerCage",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "DaysBeforeAutoClose",
                table: "Cooperatives");
        }
    }
}
