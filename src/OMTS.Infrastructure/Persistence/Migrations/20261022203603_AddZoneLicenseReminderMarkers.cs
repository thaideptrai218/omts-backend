using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddZoneLicenseReminderMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ZoneComplianceDocuments_ReplacesDocumentId",
                table: "ZoneComplianceDocuments");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiredReminderSentAt",
                table: "ZoneComplianceDocuments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiringReminderSentAt",
                table: "ZoneComplianceDocuments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZoneComplianceDocuments_ReplacesDocumentId",
                table: "ZoneComplianceDocuments",
                column: "ReplacesDocumentId",
                unique: true,
                filter: "[ReplacesDocumentId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ZoneComplianceDocuments_ReplacesDocumentId",
                table: "ZoneComplianceDocuments");

            migrationBuilder.DropColumn(
                name: "ExpiredReminderSentAt",
                table: "ZoneComplianceDocuments");

            migrationBuilder.DropColumn(
                name: "ExpiringReminderSentAt",
                table: "ZoneComplianceDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_ZoneComplianceDocuments_ReplacesDocumentId",
                table: "ZoneComplianceDocuments",
                column: "ReplacesDocumentId");
        }
    }
}
