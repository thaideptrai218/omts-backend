using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Lane incidents (decision of 12/11/2026): the damaged flag and the out-of-service day on a lane, and the history of what happened to a lane with its optional photos and the materials a
    /// repair took from the stock. No data fix: every existing lane is in service and not damaged. Down drops the tables and columns, and with them the history of the incidents.
    /// </summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261112110000_LaneIncidents")]
    public partial class LaneIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "IsDamaged", table: "Lanes", type: "bit", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<DateOnly>(name: "OutOfServiceOn", table: "Lanes", type: "date", nullable: true);

            migrationBuilder.CreateTable(
                name: "LaneIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    StateBefore = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    HadSeed = table.Column<bool>(type: "bit", nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResolvesIncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaneIncidents", x => x.Id);
                    table.CheckConstraint("CK_LaneIncident_Reason", "LEN([Reason]) > 0");
                    table.ForeignKey(
                        name: "FK_LaneIncidents_Accounts_RecordedByAccountId", column: x => x.RecordedByAccountId, principalTable: "Accounts", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneIncidents_CultivationCycles_CycleId", column: x => x.CycleId, principalTable: "CultivationCycles", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneIncidents_LaneIncidents_ResolvesIncidentId", column: x => x.ResolvesIncidentId, principalTable: "LaneIncidents", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneIncidents_Lanes_LaneId", column: x => x.LaneId, principalTable: "Lanes", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaneIncidentMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaterialUsageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    IssueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaneIncidentMaterials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LaneIncidentMaterials_FarmingInputItems_ItemId", column: x => x.ItemId, principalTable: "FarmingInputItems", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneIncidentMaterials_InputIssues_IssueId", column: x => x.IssueId, principalTable: "InputIssues", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneIncidentMaterials_LaneIncidents_IncidentId", column: x => x.IncidentId, principalTable: "LaneIncidents", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaneIncidentMaterials_MaterialUsages_MaterialUsageId", column: x => x.MaterialUsageId, principalTable: "MaterialUsages", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaneIncidentPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_LaneIncidentPhotos", x => x.Id);
                    table.CheckConstraint("CK_LaneIncidentPhoto_SortOrder", "[SortOrder] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_LaneIncidentPhotos_LaneIncidents_IncidentId", column: x => x.IncidentId, principalTable: "LaneIncidents", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(name: "IX_LaneIncidentMaterials_IncidentId", table: "LaneIncidentMaterials", column: "IncidentId");
            migrationBuilder.CreateIndex(name: "IX_LaneIncidentMaterials_IssueId", table: "LaneIncidentMaterials", column: "IssueId");
            migrationBuilder.CreateIndex(name: "IX_LaneIncidentMaterials_ItemId", table: "LaneIncidentMaterials", column: "ItemId");
            migrationBuilder.CreateIndex(name: "IX_LaneIncidentMaterials_MaterialUsageId", table: "LaneIncidentMaterials", column: "MaterialUsageId");
            migrationBuilder.CreateIndex(name: "IX_LaneIncidentPhotos_IncidentId_SortOrder", table: "LaneIncidentPhotos", columns: new[] { "IncidentId", "SortOrder" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_LaneIncidents_CycleId", table: "LaneIncidents", column: "CycleId");
            migrationBuilder.CreateIndex(name: "IX_LaneIncidents_LaneId_OccurredAt", table: "LaneIncidents", columns: new[] { "LaneId", "OccurredAt" });
            migrationBuilder.CreateIndex(name: "IX_LaneIncidents_RecordedByAccountId", table: "LaneIncidents", column: "RecordedByAccountId");
            migrationBuilder.CreateIndex(name: "IX_LaneIncidents_ResolvesIncidentId", table: "LaneIncidents", column: "ResolvesIncidentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LaneIncidentMaterials");
            migrationBuilder.DropTable(name: "LaneIncidentPhotos");
            migrationBuilder.DropTable(name: "LaneIncidents");
            migrationBuilder.DropColumn(name: "IsDamaged", table: "Lanes");
            migrationBuilder.DropColumn(name: "OutOfServiceOn", table: "Lanes");
        }
    }
}
