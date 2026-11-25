using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropHarvestPlanLanes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HarvestPlanLanes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HarvestPlanLanes",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SkipReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SkippedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SkippedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestPlanLanes", x => new { x.LineId, x.LaneId });
                    table.CheckConstraint("CK_HarvestPlanLane_Skipped", "([SkippedAt] IS NULL AND [SkipReason] IS NULL AND [SkippedByAccountId] IS NULL) OR ([SkippedAt] IS NOT NULL AND [SkipReason] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_HarvestPlanLanes_Accounts_SkippedByAccountId",
                        column: x => x.SkippedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanLanes_HarvestPlanLines_LineId",
                        column: x => x.LineId,
                        principalTable: "HarvestPlanLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanLanes_Lanes_LaneId",
                        column: x => x.LaneId,
                        principalTable: "Lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLanes_LaneId",
                table: "HarvestPlanLanes",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLanes_SkippedByAccountId",
                table: "HarvestPlanLanes",
                column: "SkippedByAccountId");
        }
    }
}
