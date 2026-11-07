using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PR C2 (SRS 27/10, CYCLE-02): a seeding session names its seed lot, its whole lanes and a required quantity. It replaces the old seeding events, which named no lane, so
    /// there is nothing to move: stored data is dev and demo only, and the demo never wrote any. Down gives back the empty old table and drops the sessions.
    /// </summary>
    public partial class SeedingRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeedingEvents");

            migrationBuilder.CreateTable(
                name: "SeedingRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeedingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SeedLotLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_SeedingRecords", x => x.Id);
                    table.CheckConstraint("CK_SeedingRecord_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_SeedingRecords_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedingRecords_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedingRecords_PurchaseReceiptLines_SeedLotLineId",
                        column: x => x.SeedLotLineId,
                        principalTable: "PurchaseReceiptLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeedingRecordLanes",
                columns: table => new
                {
                    SeedingRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeedingRecordLanes", x => new { x.SeedingRecordId, x.LaneId });
                    table.ForeignKey(
                        name: "FK_SeedingRecordLanes_Lanes_LaneId",
                        column: x => x.LaneId,
                        principalTable: "Lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedingRecordLanes_SeedingRecords_SeedingRecordId",
                        column: x => x.SeedingRecordId,
                        principalTable: "SeedingRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeedingRecordLanes_LaneId",
                table: "SeedingRecordLanes",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedingRecords_CycleId_SeedingDate",
                table: "SeedingRecords",
                columns: new[] { "CycleId", "SeedingDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SeedingRecords_RecordedByAccountId",
                table: "SeedingRecords",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedingRecords_SeedLotLineId",
                table: "SeedingRecords",
                column: "SeedLotLineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeedingRecordLanes");

            migrationBuilder.DropTable(
                name: "SeedingRecords");

            migrationBuilder.CreateTable(
                name: "SeedingEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeedLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PlanDeviationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    SeedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    SeedingDate = table.Column<DateOnly>(type: "date", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Workers = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeedingEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeedingEvents_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedingEvents_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedingEvents_PurchaseReceiptLines_SeedLotId",
                        column: x => x.SeedLotId,
                        principalTable: "PurchaseReceiptLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeedingEvents_CycleId",
                table: "SeedingEvents",
                column: "CycleId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedingEvents_RecordedByAccountId",
                table: "SeedingEvents",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedingEvents_SeedLotId",
                table: "SeedingEvents",
                column: "SeedLotId");
        }
    }
}
