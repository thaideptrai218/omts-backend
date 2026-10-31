using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_ActorAccountId",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_RecordType_RecordId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "ViewScope",
                table: "AuditEvents");

            migrationBuilder.AddColumn<Guid>(
                name: "ActorEmployeeId",
                table: "AuditEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "Action", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorAccountId_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "ActorAccountId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorEmployeeId_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "ActorEmployeeId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_RecordCode_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "RecordCode", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_RecordType_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "RecordType", "OccurredAt" });

            // Notifications and audit events name zone licenses the same way; the audit log is never edited, so the
            // notification rows take its name.
            migrationBuilder.Sql(
                "UPDATE Notifications SET RelatedRecordType = N'ZoneLicense' WHERE RelatedRecordType = N'ZoneComplianceDocument';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE Notifications SET RelatedRecordType = N'ZoneComplianceDocument' WHERE RelatedRecordType = N'ZoneLicense';");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_Action_OccurredAt",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_ActorAccountId_OccurredAt",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_ActorEmployeeId_OccurredAt",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_RecordCode_OccurredAt",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_RecordType_OccurredAt",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "ActorEmployeeId",
                table: "AuditEvents");

            migrationBuilder.AddColumn<string>(
                name: "ViewScope",
                table: "AuditEvents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorAccountId",
                table: "AuditEvents",
                column: "ActorAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_RecordType_RecordId",
                table: "AuditEvents",
                columns: new[] { "RecordType", "RecordId" });
        }
    }
}
