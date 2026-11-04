using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PR 0.9 (SRS 27/10, FARM-04): a raft holds up to three RaftPhotos instead of one PhotoUrl (the old photo becomes the
    /// first), a lane gets the status In Use or Removed (new LANE group) and loses its length and markers, and the layout
    /// versions, lane segments and cultivation log entries go. Stored data is dev and demo only: every existing lane becomes
    /// In Use. Down keeps the first photo of each raft and deletes the Removed lanes, as the old model has neither more photos
    /// nor removed lanes; the dropped tables and lane columns come back empty.
    /// </summary>
    public partial class RaftsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LaneSegments");

            migrationBuilder.DropTable(
                name: "RaftLayoutVersions");

            migrationBuilder.DropTable(
                name: "CultivationLogEntries");

            migrationBuilder.DropColumn(
                name: "Length",
                table: "Lanes");

            migrationBuilder.DropColumn(
                name: "MeasurementMarkers",
                table: "Lanes");

            migrationBuilder.AddColumn<int>(
                name: "StatusId",
                table: "Lanes",
                type: "int",
                nullable: false,
                defaultValue: 2001);

            migrationBuilder.CreateTable(
                name: "RaftPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_RaftPhotos", x => x.Id);
                    table.CheckConstraint("CK_RaftPhoto_SortOrder", "[SortOrder] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_RaftPhotos_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                INSERT INTO RaftPhotos (Id, RaftId, SortOrder, FileKey, CreatedAt)
                SELECT NEWID(), Id, 1, PhotoUrl, SYSDATETIMEOFFSET() FROM Rafts WHERE PhotoUrl IS NOT NULL AND PhotoUrl <> '';
                """);

            migrationBuilder.DropColumn(
                name: "PhotoUrl",
                table: "Rafts");

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 2001, "IN_USE", null, "In Use", "LANE", false, true, "Seeded and harvested.", 1 },
                    { 2002, "REMOVED", null, "Removed", "LANE", false, false, "Removed at the end of the raft; keeps its number and records.", 2 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 76, "Cooperative Owner", 2001, "The raft has no open cycle; the last lanes are removed first (BR-FARM-04).", false, 2002, "Action", "FARM-04" },
                    { 77, "Cooperative Owner", 2002, "The raft has no open cycle; Removed lanes return in number order before new numbers (BR-FARM-04).", false, 2001, "Action", "FARM-04" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lanes_StatusId",
                table: "Lanes",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_RaftPhotos_RaftId_SortOrder",
                table: "RaftPhotos",
                columns: new[] { "RaftId", "SortOrder" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Lanes_Statuses_StatusId",
                table: "Lanes",
                column: "StatusId",
                principalTable: "Statuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Lanes_Statuses_StatusId",
                table: "Lanes");

            migrationBuilder.AddColumn<string>(
                name: "PhotoUrl",
                table: "Rafts",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE r SET PhotoUrl = (SELECT TOP (1) p.FileKey FROM RaftPhotos p WHERE p.RaftId = r.Id ORDER BY p.SortOrder)
                FROM Rafts r;
                DELETE FROM Lanes WHERE StatusId = 2002;
                """);

            migrationBuilder.DropTable(
                name: "RaftPhotos");

            migrationBuilder.DropIndex(
                name: "IX_Lanes_StatusId",
                table: "Lanes");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 76);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 77);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2001);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2002);

            migrationBuilder.DropColumn(
                name: "StatusId",
                table: "Lanes");

            migrationBuilder.AddColumn<decimal>(
                name: "Length",
                table: "Lanes",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeasurementMarkers",
                table: "Lanes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CultivationLogEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LanePosition = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Observation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Observer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CultivationLogEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CultivationLogEntries_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CultivationLogEntries_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CultivationLogEntries_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CultivationLogEntries_Lanes_LaneId",
                        column: x => x.LaneId,
                        principalTable: "Lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaftLayoutVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LaneCount = table.Column<int>(type: "int", nullable: false),
                    LayoutMap = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    VersionNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaftLayoutVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaftLayoutVersions_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaneSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LogId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SeedingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfirmedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromPosition = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    PlanDeviationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PlannedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    SegmentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SegmentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ToPosition = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WriteOffPhotoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    WriteOffReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaneSegments", x => x.Id);
                    table.CheckConstraint("CK_LaneSegment_Positions", "[ToPosition] > [FromPosition] AND [FromPosition] >= 0");
                    table.ForeignKey(
                        name: "FK_LaneSegments_Accounts_ConfirmedByAccountId",
                        column: x => x.ConfirmedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_CultivationCycles_CycleId",
                        column: x => x.CycleId,
                        principalTable: "CultivationCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_CultivationLogEntries_LogId",
                        column: x => x.LogId,
                        principalTable: "CultivationLogEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_Lanes_LaneId",
                        column: x => x.LaneId,
                        principalTable: "Lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneSegments_SeedingEvents_SeedingId",
                        column: x => x.SeedingId,
                        principalTable: "SeedingEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CultivationLogEntries_CycleId",
                table: "CultivationLogEntries",
                column: "CycleId");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationLogEntries_LaneId",
                table: "CultivationLogEntries",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationLogEntries_RecordedByAccountId",
                table: "CultivationLogEntries",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationLogEntries_ZoneId_OccurredAt",
                table: "CultivationLogEntries",
                columns: new[] { "ZoneId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_ConfirmedByAccountId",
                table: "LaneSegments",
                column: "ConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_CycleId_LaneId_SegmentType",
                table: "LaneSegments",
                columns: new[] { "CycleId", "LaneId", "SegmentType" });

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_LaneId",
                table: "LaneSegments",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_LogId",
                table: "LaneSegments",
                column: "LogId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_RecordedByAccountId",
                table: "LaneSegments",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LaneSegments_SeedingId",
                table: "LaneSegments",
                column: "SeedingId");

            migrationBuilder.CreateIndex(
                name: "IX_RaftLayoutVersions_RaftId_VersionNumber",
                table: "RaftLayoutVersions",
                columns: new[] { "RaftId", "VersionNumber" },
                unique: true);
        }
    }
}
