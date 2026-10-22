using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditZoneIndexAndStatusTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 3,
                column: "UseCases",
                value: "FARM-01");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "AllowedPerformers", "UseCases" },
                values: new object[] { "Cooperative Owner (staff accounts); System Administrator (trader accounts, platform administration outside the cooperative app)", "ACS-09" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner (staff accounts); System Administrator (trader accounts, platform administration outside the cooperative app)", "Unlock after verification; trader accounts are unlocked only by the System Administrator.", "ACS-09" });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 201,
                column: "Meaning",
                value: "Created at deployment; the Cooperative Owner has not completed the legal profile yet.");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ZoneId_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "ZoneId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_ZoneId_OccurredAt",
                table: "AuditEvents");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 3,
                column: "UseCases",
                value: "UC09");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "AllowedPerformers", "UseCases" },
                values: new object[] { "Cooperative Owner; System Administrator", "UC11, UC74" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner; System Administrator", "Unlock after verification; the System Administrator unlocks only accounts it locked itself.", "UC11, UC74" });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 201,
                column: "Meaning",
                value: "The System Administrator has just set up the cooperative; the profile lacks legal information.");
        }
    }
}
