using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NurseryBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "NurseryBatchId",
                table: "SeedingRecords",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NurseryAllocatedQuantity",
                table: "CultivationCycles",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NurseryBatchId",
                table: "CultivationCycles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NurseryBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeedLotLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuantityIn = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CompletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Survivors = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    CompletedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IssueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NurseryBatches", x => x.Id);
                    table.CheckConstraint("CK_NurseryBatch_QuantityIn", "[QuantityIn] > 0");
                    table.CheckConstraint("CK_NurseryBatch_Survivors", "[Survivors] IS NULL OR ([Survivors] >= 0 AND [Survivors] <= [QuantityIn])");
                    table.ForeignKey(
                        name: "FK_NurseryBatches_Accounts_CompletedByAccountId",
                        column: x => x.CompletedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NurseryBatches_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NurseryBatches_InputIssues_IssueId",
                        column: x => x.IssueId,
                        principalTable: "InputIssues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NurseryBatches_PurchaseReceiptLines_SeedLotLineId",
                        column: x => x.SeedLotLineId,
                        principalTable: "PurchaseReceiptLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NurseryBatches_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NurseryBatchAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NurseryBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NurseryBatchAllocations", x => x.Id);
                    table.CheckConstraint("CK_NurseryBatchAllocation_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_NurseryBatchAllocations_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NurseryBatchAllocations_NurseryBatches_NurseryBatchId",
                        column: x => x.NurseryBatchId,
                        principalTable: "NurseryBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NurseryBatchAllocations_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NurseryBatchPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NurseryBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
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
                    table.PrimaryKey("PK_NurseryBatchPhotos", x => x.Id);
                    table.CheckConstraint("CK_NurseryBatchPhoto_SortOrder", "[SortOrder] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_NurseryBatchPhotos_NurseryBatches_NurseryBatchId",
                        column: x => x.NurseryBatchId,
                        principalTable: "NurseryBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "InputIssueTypes",
                columns: new[] { "Id", "CreatedAt", "CreatedBy", "IsActive", "IsSystem", "Name", "UpdatedAt", "UpdatedBy" },
                values: new object[] { new Guid("6e1f0b20-0000-4d4a-8b20-000000000004"), new DateTimeOffset(new DateTime(2026, 11, 11, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, true, true, "Ươm giống", null, null });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 4001, "NURSING", null, "Nursing", "NURSERY_BATCH", false, true, "The pieces left the stock and are in the nursery; it can be edited or cancelled.", 1 },
                    { 4002, "COMPLETED", null, "Completed", "NURSERY_BATCH", true, false, "The survivors are recorded and each raft has its cycle; the survivors and shares can still be corrected before the seed is hung.", 2 },
                    { 4003, "CANCELLED", null, "Cancelled", "NURSERY_BATCH", true, false, "Recorded by mistake before it was completed; the pieces are back in the stock.", 3 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30090, "Cooperative Owner; Zone Manager", 4001, "The survivors are not above the pieces put in, and the shares of the rafts of the zone, none with an Open cycle, add up to the survivors exactly.", false, 4002, "Action", "NURSERY-03" },
                    { 30091, "Cooperative Owner; Zone Manager", 4001, "The batch is still Nursing; the pieces go back in the stock.", true, 4003, "Action", "NURSERY-02" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeedingRecords_NurseryBatchId",
                table: "SeedingRecords",
                column: "NurseryBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationCycles_NurseryBatchId",
                table: "CultivationCycles",
                column: "NurseryBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatchAllocations_CycleId",
                table: "NurseryBatchAllocations",
                column: "CycleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatchAllocations_NurseryBatchId_RaftId",
                table: "NurseryBatchAllocations",
                columns: new[] { "NurseryBatchId", "RaftId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatchAllocations_RaftId",
                table: "NurseryBatchAllocations",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatches_Code",
                table: "NurseryBatches",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatches_CompletedByAccountId",
                table: "NurseryBatches",
                column: "CompletedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatches_IssueId",
                table: "NurseryBatches",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatches_SeedLotLineId",
                table: "NurseryBatches",
                column: "SeedLotLineId");

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatches_StatusId",
                table: "NurseryBatches",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatches_ZoneId_StatusId_StartDate",
                table: "NurseryBatches",
                columns: new[] { "ZoneId", "StatusId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatchPhotos_NurseryBatchId_Phase_SortOrder",
                table: "NurseryBatchPhotos",
                columns: new[] { "NurseryBatchId", "Phase", "SortOrder" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CultivationCycles_NurseryBatches_NurseryBatchId",
                table: "CultivationCycles",
                column: "NurseryBatchId",
                principalTable: "NurseryBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeedingRecords_NurseryBatches_NurseryBatchId",
                table: "SeedingRecords",
                column: "NurseryBatchId",
                principalTable: "NurseryBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CultivationCycles_NurseryBatches_NurseryBatchId",
                table: "CultivationCycles");

            migrationBuilder.DropForeignKey(
                name: "FK_SeedingRecords_NurseryBatches_NurseryBatchId",
                table: "SeedingRecords");

            migrationBuilder.DropTable(
                name: "NurseryBatchAllocations");

            migrationBuilder.DropTable(
                name: "NurseryBatchPhotos");

            migrationBuilder.DropTable(
                name: "NurseryBatches");

            migrationBuilder.DropIndex(
                name: "IX_SeedingRecords_NurseryBatchId",
                table: "SeedingRecords");

            migrationBuilder.DropIndex(
                name: "IX_CultivationCycles_NurseryBatchId",
                table: "CultivationCycles");

            migrationBuilder.DeleteData(
                table: "InputIssueTypes",
                keyColumn: "Id",
                keyValue: new Guid("6e1f0b20-0000-4d4a-8b20-000000000004"));

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30090);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30091);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4001);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4002);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4003);

            migrationBuilder.DropColumn(
                name: "NurseryBatchId",
                table: "SeedingRecords");

            migrationBuilder.DropColumn(
                name: "NurseryAllocatedQuantity",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "NurseryBatchId",
                table: "CultivationCycles");
        }
    }
}
