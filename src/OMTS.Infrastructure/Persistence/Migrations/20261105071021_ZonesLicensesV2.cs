using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PR 0.8 (SRS 27/10): one zone per license (FARM-03) instead of the DocumentZoneScopes rows, an optional license type,
    /// and the GFR-03 reminder markers of the cooperative certifications. Stored data is dev and demo only, so a license that
    /// covered several zones keeps one of them (the lowest zone code) instead of being split; Down gives every license one
    /// scope row again and a type of OTHER where it has none.
    /// </summary>
    public partial class ZonesLicensesV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiredReminderSentAt",
                table: "CooperativeCertifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiringReminderSentAt",
                table: "CooperativeCertifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                table: "ZoneComplianceDocuments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE d SET ZoneId = (
                    SELECT TOP (1) s.ZoneId
                    FROM DocumentZoneScopes s JOIN FarmingZones z ON z.Id = s.ZoneId
                    WHERE s.DocumentId = d.Id
                    ORDER BY z.Code, s.ZoneId)
                FROM ZoneComplianceDocuments d;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "ZoneId",
                table: "ZoneComplianceDocuments",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZoneComplianceDocuments_ZoneId",
                table: "ZoneComplianceDocuments",
                column: "ZoneId");

            migrationBuilder.AddForeignKey(
                name: "FK_ZoneComplianceDocuments_FarmingZones_ZoneId",
                table: "ZoneComplianceDocuments",
                column: "ZoneId",
                principalTable: "FarmingZones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AlterColumn<string>(
                name: "DocumentType",
                table: "ZoneComplianceDocuments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.DropTable(
                name: "DocumentZoneScopes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentZoneScopes",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentZoneScopes", x => new { x.DocumentId, x.ZoneId });
                    table.ForeignKey(
                        name: "FK_DocumentZoneScopes_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentZoneScopes_ZoneComplianceDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "ZoneComplianceDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentZoneScopes_ZoneId",
                table: "DocumentZoneScopes",
                column: "ZoneId");

            migrationBuilder.Sql("""
                INSERT INTO DocumentZoneScopes (DocumentId, ZoneId, ValidFrom, ValidTo)
                SELECT Id, ZoneId, EffectiveDate, ExpiryDate FROM ZoneComplianceDocuments;
                UPDATE ZoneComplianceDocuments SET DocumentType = 'OTHER' WHERE DocumentType IS NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ZoneComplianceDocuments_FarmingZones_ZoneId",
                table: "ZoneComplianceDocuments");

            migrationBuilder.DropIndex(
                name: "IX_ZoneComplianceDocuments_ZoneId",
                table: "ZoneComplianceDocuments");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "ZoneComplianceDocuments");

            migrationBuilder.DropColumn(
                name: "ExpiredReminderSentAt",
                table: "CooperativeCertifications");

            migrationBuilder.DropColumn(
                name: "ExpiringReminderSentAt",
                table: "CooperativeCertifications");

            migrationBuilder.AlterColumn<string>(
                name: "DocumentType",
                table: "ZoneComplianceDocuments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);
        }
    }
}
