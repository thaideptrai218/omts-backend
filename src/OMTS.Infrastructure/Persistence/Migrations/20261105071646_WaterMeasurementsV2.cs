using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WaterMeasurementsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Water data is dev and demo data only. A measurement now needs a raft, a temperature and a meter photo, which the
            // stored rows do not have, and their statuses go away: the rows are removed and the demo seed records them again.
            migrationBuilder.Sql("DELETE FROM [WaterMeasurements];");

            migrationBuilder.DropForeignKey(
                name: "FK_WaterMeasurements_Accounts_ConfirmedByAccountId",
                table: "WaterMeasurements");

            migrationBuilder.DropForeignKey(
                name: "FK_WaterMeasurements_WaterMeasurements_CorrectedFromMeasurementId",
                table: "WaterMeasurements");

            migrationBuilder.DropIndex(
                name: "IX_WaterMeasurements_ConfirmedByAccountId",
                table: "WaterMeasurements");

            migrationBuilder.DropIndex(
                name: "IX_WaterMeasurements_CorrectedFromMeasurementId",
                table: "WaterMeasurements");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 11);

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
                keyValue: 701);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 702);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 703);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 704);

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "ConfirmedByAccountId",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "CorrectedFromMeasurementId",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "CorrectionReason",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "InputSource",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "MeasurementPoint",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "OcrMeasuredAt",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "OcrPh",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "OcrSalinity",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "RecordVersion",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "WaterMeasurementPoint",
                table: "FarmingZones");

            migrationBuilder.AlterColumn<decimal>(
                name: "Salinity",
                table: "WaterMeasurements",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,2)",
                oldPrecision: 5,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PhotoUrl",
                table: "WaterMeasurements",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Ph",
                table: "WaterMeasurements",
                type: "decimal(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(4,2)",
                oldPrecision: 4,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RaftId",
                table: "WaterMeasurements",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<decimal>(
                name: "Temperature",
                table: "WaterMeasurements",
                type: "decimal(4,2)",
                precision: 4,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureMaxUsed",
                table: "WaterMeasurements",
                type: "decimal(4,2)",
                precision: 4,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureMinUsed",
                table: "WaterMeasurements",
                type: "decimal(4,2)",
                precision: 4,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureMax",
                table: "FarmingZones",
                type: "decimal(4,2)",
                precision: 4,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TemperatureMin",
                table: "FarmingZones",
                type: "decimal(4,2)",
                precision: 4,
                scale: 2,
                nullable: true);

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 2101, "ACTIVE", null, "Active", "WATER_MEASUREMENT_RECORD", false, true, "Counts in the charts and the water alert of the zone.", 1 },
                    { 2102, "VOIDED", null, "Voided", "WATER_MEASUREMENT_RECORD", true, false, "Kept in the history with its reason; no longer counts.", 2 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 80, "Zone Manager (own); Cooperative Owner", 2101, "Void a wrong measurement with a reason.", true, 2102, "Action", "WATER-02" });

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_PhotoUrl",
                table: "WaterMeasurements",
                column: "PhotoUrl",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_RaftId",
                table: "WaterMeasurements",
                column: "RaftId");

            migrationBuilder.AddForeignKey(
                name: "FK_WaterMeasurements_Rafts_RaftId",
                table: "WaterMeasurements",
                column: "RaftId",
                principalTable: "Rafts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dev and demo data only, as in Up: the rows hold statuses that go away and no input source.
            migrationBuilder.Sql("DELETE FROM [WaterMeasurements];");

            migrationBuilder.DropForeignKey(
                name: "FK_WaterMeasurements_Rafts_RaftId",
                table: "WaterMeasurements");

            migrationBuilder.DropIndex(
                name: "IX_WaterMeasurements_PhotoUrl",
                table: "WaterMeasurements");

            migrationBuilder.DropIndex(
                name: "IX_WaterMeasurements_RaftId",
                table: "WaterMeasurements");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 80);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2101);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 2102);

            migrationBuilder.DropColumn(
                name: "RaftId",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "TemperatureMaxUsed",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "TemperatureMinUsed",
                table: "WaterMeasurements");

            migrationBuilder.DropColumn(
                name: "TemperatureMax",
                table: "FarmingZones");

            migrationBuilder.DropColumn(
                name: "TemperatureMin",
                table: "FarmingZones");

            migrationBuilder.AlterColumn<decimal>(
                name: "Salinity",
                table: "WaterMeasurements",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,2)",
                oldPrecision: 5,
                oldScale: 2);

            migrationBuilder.AlterColumn<string>(
                name: "PhotoUrl",
                table: "WaterMeasurements",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<decimal>(
                name: "Ph",
                table: "WaterMeasurements",
                type: "decimal(4,2)",
                precision: 4,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(4,2)",
                oldPrecision: 4,
                oldScale: 2);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAt",
                table: "WaterMeasurements",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConfirmedByAccountId",
                table: "WaterMeasurements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CorrectedFromMeasurementId",
                table: "WaterMeasurements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrectionReason",
                table: "WaterMeasurements",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputSource",
                table: "WaterMeasurements",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MeasurementPoint",
                table: "WaterMeasurements",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OcrMeasuredAt",
                table: "WaterMeasurements",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OcrPh",
                table: "WaterMeasurements",
                type: "decimal(4,2)",
                precision: 4,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OcrSalinity",
                table: "WaterMeasurements",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecordVersion",
                table: "WaterMeasurements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WaterMeasurementPoint",
                table: "FarmingZones",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 701, "DRAFT", null, "Draft", "WATER_MEASUREMENT", false, true, "OCR or manually entered values not yet confirmed; not used for reports or alerts.", 1 },
                    { 702, "CONFIRMED", null, "Confirmed", "WATER_MEASUREMENT", false, false, "Checked against the photo and confirmed; used for alerts and reports.", 2 },
                    { 703, "SUPERSEDED", null, "Corrected", "WATER_MEASUREMENT", true, false, "Replaced by a new correction version; the old record is kept for reference.", 3 },
                    { 704, "VOIDED", null, "Voided", "WATER_MEASUREMENT", true, false, "Recorded by mistake and voided with a reason; excluded from charts, reports, exports and the overdue check.", 4 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 10, "Cooperative Owner; Zone Manager", 701, "Check against the photo and confirm the values.", false, 702, "Action", "UC26" },
                    { 11, "Cooperative Owner; Zone Manager", 702, "Save a new correction version.", true, 703, "Action", "UC26" },
                    { 55, "Cooperative Owner; Zone Manager", 701, "Void a wrong draft.", true, 704, "Action", "ENV-02" },
                    { 56, "Cooperative Owner; Zone Manager", 702, "Void the latest version of a wrong measurement.", true, 704, "Action", "ENV-02" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_ConfirmedByAccountId",
                table: "WaterMeasurements",
                column: "ConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_CorrectedFromMeasurementId",
                table: "WaterMeasurements",
                column: "CorrectedFromMeasurementId",
                unique: true,
                filter: "[CorrectedFromMeasurementId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_WaterMeasurements_Accounts_ConfirmedByAccountId",
                table: "WaterMeasurements",
                column: "ConfirmedByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WaterMeasurements_WaterMeasurements_CorrectedFromMeasurementId",
                table: "WaterMeasurements",
                column: "CorrectedFromMeasurementId",
                principalTable: "WaterMeasurements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
