using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HarvestPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HarvestPlanLineId",
                table: "HarvestLots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HarvestPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AssignedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Accounts_AssignedByAccountId",
                        column: x => x.AssignedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Accounts_CancelledByAccountId",
                        column: x => x.CancelledByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Accounts_CompletedByAccountId",
                        column: x => x.CompletedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Accounts_CreatedByAccountId",
                        column: x => x.CreatedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HarvestPlanLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpectedCageCount = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestPlanLines", x => x.Id);
                    table.CheckConstraint("CK_HarvestPlanLine_ExpectedCageCount", "[ExpectedCageCount] IS NULL OR [ExpectedCageCount] > 0");
                    table.ForeignKey(
                        name: "FK_HarvestPlanLines_HarvestPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "HarvestPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanLines_Products_ExpectedProductId",
                        column: x => x.ExpectedProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanLines_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderHarvestPlans",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderHarvestPlans", x => new { x.OrderId, x.PlanId });
                    table.ForeignKey(
                        name: "FK_SalesOrderHarvestPlans_HarvestPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "HarvestPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderHarvestPlans_SalesOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HarvestPlanLanes",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SkippedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SkippedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SkipReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
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

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 4201, "DRAFT", null, "Draft", "HARVEST_PLAN", false, true, "Being prepared by the Owner; the Zone Managers do not see it yet.", 1 },
                    { 4202, "ASSIGNED", null, "Assigned", "HARVEST_PLAN", false, false, "Handed to the Zone Managers of its zone; no lot is recorded and no lane left out yet.", 2 },
                    { 4203, "HARVESTING", null, "Harvesting", "HARVEST_PLAN", false, false, "A lot is recorded from a line of the plan, or a lane is left out.", 3 },
                    { 4204, "COMPLETED", null, "Completed", "HARVEST_PLAN", false, false, "Every line has its lot or has every lane left out, or the Owner finished the plan by hand; removing a lot of a plan finished by itself opens it again.", 4 },
                    { 4205, "CANCELLED", null, "Cancelled", "HARVEST_PLAN", true, false, "Cancelled with a reason before any lot was recorded.", 5 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30092, "Cooperative Owner", 4201, "The plan has lines, every raft has an Open cycle and no lane is in another open plan; the Zone Managers of the zone are told.", false, 4202, "Action", "HARVEST-PLAN" },
                    { 30093, "System", 4202, "The first lot is recorded from a line of the plan, or the first lane is left out.", false, 4203, "Action", "HARVEST-PLAN" },
                    { 30094, "Cooperative Owner; System", 4203, "Every line has its lot or every lane left out, or the Owner finishes the plan: lanes still to do stay as they are.", false, 4204, "Action", "HARVEST-PLAN" },
                    { 30095, "System", 4202, "Every lane of every line is left out and no lot was recorded.", false, 4204, "Action", "HARVEST-PLAN" },
                    { 30096, "Cooperative Owner", 4201, "A reason is given.", true, 4205, "Action", "HARVEST-PLAN" },
                    { 30097, "Cooperative Owner", 4202, "A reason is given and no lot was recorded from the plan.", true, 4205, "Action", "HARVEST-PLAN" },
                    { 30098, "System", 4204, "A lot of a plan that finished by itself is removed, or a lane left out is put back: a line is open again.", false, 4203, "Action", "HARVEST-PLAN" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_HarvestPlanLineId",
                table: "HarvestLots",
                column: "HarvestPlanLineId",
                unique: true,
                filter: "[HarvestPlanLineId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLanes_LaneId",
                table: "HarvestPlanLanes",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLanes_SkippedByAccountId",
                table: "HarvestPlanLanes",
                column: "SkippedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLines_ExpectedProductId",
                table: "HarvestPlanLines",
                column: "ExpectedProductId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLines_PlanId_LineNumber",
                table: "HarvestPlanLines",
                columns: new[] { "PlanId", "LineNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLines_RaftId",
                table: "HarvestPlanLines",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_AssignedByAccountId",
                table: "HarvestPlans",
                column: "AssignedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_CancelledByAccountId",
                table: "HarvestPlans",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_Code",
                table: "HarvestPlans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_CompletedByAccountId",
                table: "HarvestPlans",
                column: "CompletedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_CreatedByAccountId",
                table: "HarvestPlans",
                column: "CreatedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_StatusId_PlannedDate",
                table: "HarvestPlans",
                columns: new[] { "StatusId", "PlannedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_ZoneId",
                table: "HarvestPlans",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderHarvestPlans_PlanId",
                table: "SalesOrderHarvestPlans",
                column: "PlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestLots_HarvestPlanLines_HarvestPlanLineId",
                table: "HarvestLots",
                column: "HarvestPlanLineId",
                principalTable: "HarvestPlanLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM DocumentSequences WHERE [Key] = N'KHT') INSERT INTO DocumentSequences ([Key], LastValue) VALUES (N'KHT', 0);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM DocumentSequences WHERE [Key] = N'KHT';");

            migrationBuilder.DropForeignKey(
                name: "FK_HarvestLots_HarvestPlanLines_HarvestPlanLineId",
                table: "HarvestLots");

            migrationBuilder.DropTable(
                name: "HarvestPlanLanes");

            migrationBuilder.DropTable(
                name: "SalesOrderHarvestPlans");

            migrationBuilder.DropTable(
                name: "HarvestPlanLines");

            migrationBuilder.DropTable(
                name: "HarvestPlans");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLots_HarvestPlanLineId",
                table: "HarvestLots");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30092);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30093);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30094);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30095);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30096);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30097);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30098);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4201);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4202);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4203);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4204);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4205);

            migrationBuilder.DropColumn(
                name: "HarvestPlanLineId",
                table: "HarvestLots");
        }
    }
}
