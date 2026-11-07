using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWaterMeasurementOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WaterMeasurements_CorrectedFromMeasurementId",
                table: "WaterMeasurements");

            migrationBuilder.DropIndex(
                name: "IX_WaterMeasurements_MeasuredByAccountId",
                table: "WaterMeasurements");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientOperationId",
                table: "WaterMeasurements",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Rows recorded before this migration get their own operation id, so the unique index below holds.
            migrationBuilder.Sql(
                "UPDATE [WaterMeasurements] SET [ClientOperationId] = NEWID() WHERE [ClientOperationId] = '00000000-0000-0000-0000-000000000000';");

            migrationBuilder.AddColumn<string>(
                name: "VoidReason",
                table: "WaterMeasurements",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WaterOverdueRemindedAt",
                table: "FarmingZones",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[] { 704, "VOIDED", null, "Voided", "WATER_MEASUREMENT", true, false, "Recorded by mistake and voided with a reason; excluded from charts, reports, exports and the overdue check.", 4 });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 55, "Cooperative Owner; Zone Manager", 701, "Void a wrong draft.", true, 704, "Action", "ENV-02" },
                    { 56, "Cooperative Owner; Zone Manager", 702, "Void the latest version of a wrong measurement.", true, 704, "Action", "ENV-02" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_CorrectedFromMeasurementId",
                table: "WaterMeasurements",
                column: "CorrectedFromMeasurementId",
                unique: true,
                filter: "[CorrectedFromMeasurementId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_MeasuredByAccountId_ClientOperationId",
                table: "WaterMeasurements",
                columns: new[] { "MeasuredByAccountId", "ClientOperationId" },
                unique: true,
                filter: "[MeasuredByAccountId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WaterMeasurements_CorrectedFromMeasurementId",
                table: "WaterMeasurements");

            migrationBuilder.DropIndex(
                name: "IX_WaterMeasurements_MeasuredByAccountId_ClientOperationId",
                table: "WaterMeasurements");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 55);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 56);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 704);

            migrationBuilder.DropColumn(
                name: "ClientOperationId",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "VoidReason",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "WaterOverdueRemindedAt",
                table: "FarmingZones");

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_CorrectedFromMeasurementId",
                table: "WaterMeasurements",
                column: "CorrectedFromMeasurementId");

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_MeasuredByAccountId",
                table: "WaterMeasurements",
                column: "MeasuredByAccountId");
        }
    }
}
