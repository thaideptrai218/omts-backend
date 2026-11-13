using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LaneProgressPercentAndStepPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SeededPercent",
                table: "SeedingRecordLanes",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 100m);

            migrationBuilder.AddColumn<decimal>(
                name: "HarvestedPercent",
                table: "HarvestLotLanes",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            // Backfill (owner decision of 12/11/2026): a lane already hung counts as 100 % (the SeededPercent default above does it), a lane marked finished is 100 % harvested, and a lane harvested only in part
            // stays null (nobody entered the share).
            migrationBuilder.Sql("UPDATE [HarvestLotLanes] SET [HarvestedPercent] = 100 WHERE [IsFinished] = 1");

            migrationBuilder.CreateTable(
                name: "CyclePlanPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_CyclePlanPhotos", x => x.Id);
                    table.CheckConstraint("CK_CyclePlanPhoto_SortOrder", "[SortOrder] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_CyclePlanPhotos_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaneProgressChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ChangedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OldSeededPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    OldHarvestedPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    SeededPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    HarvestedPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaneProgressChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LaneProgressChanges_Accounts_ChangedByAccountId",
                        column: x => x.ChangedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneProgressChanges_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneProgressChanges_Lanes_LaneId",
                        column: x => x.LaneId,
                        principalTable: "Lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeedingRecordPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeedingRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_SeedingRecordPhotos", x => x.Id);
                    table.CheckConstraint("CK_SeedingRecordPhoto_SortOrder", "[SortOrder] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_SeedingRecordPhotos_SeedingRecords_SeedingRecordId",
                        column: x => x.SeedingRecordId,
                        principalTable: "SeedingRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SeedingRecordLane_SeededPercent",
                table: "SeedingRecordLanes",
                sql: "[SeededPercent] > 0 AND [SeededPercent] <= 100");

            migrationBuilder.AddCheckConstraint(
                name: "CK_HarvestLotLane_HarvestedPercent",
                table: "HarvestLotLanes",
                sql: "[HarvestedPercent] IS NULL OR ([HarvestedPercent] > 0 AND [HarvestedPercent] <= 100)");

            migrationBuilder.CreateIndex(
                name: "IX_CyclePlanPhotos_CycleId_SortOrder",
                table: "CyclePlanPhotos",
                columns: new[] { "CycleId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LaneProgressChanges_ChangedByAccountId",
                table: "LaneProgressChanges",
                column: "ChangedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneProgressChanges_CycleId_LaneId_OccurredAt",
                table: "LaneProgressChanges",
                columns: new[] { "CycleId", "LaneId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LaneProgressChanges_LaneId",
                table: "LaneProgressChanges",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedingRecordPhotos_SeedingRecordId_SortOrder",
                table: "SeedingRecordPhotos",
                columns: new[] { "SeedingRecordId", "SortOrder" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CyclePlanPhotos");

            migrationBuilder.DropTable(
                name: "LaneProgressChanges");

            migrationBuilder.DropTable(
                name: "SeedingRecordPhotos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SeedingRecordLane_SeededPercent",
                table: "SeedingRecordLanes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_HarvestLotLane_HarvestedPercent",
                table: "HarvestLotLanes");

            migrationBuilder.DropColumn(
                name: "SeededPercent",
                table: "SeedingRecordLanes");

            migrationBuilder.DropColumn(
                name: "HarvestedPercent",
                table: "HarvestLotLanes");
        }
    }
}
