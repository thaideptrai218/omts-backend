using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PR C1 (SRS 27/10, CYCLE-01): a cycle has a start and a close date instead of two times, with optional plan estimates, and
    /// the fields of the close. Stored data is dev and demo only, so only what the schema needs moves: the start date and the close
    /// date of a Closed cycle, as business dates (UTC+7). The planned seed type, the seeding time and reason and the closing summary
    /// go; they are not renamed, as a seeding time is no close reminder and a summary is no assessment (BR-CYCLE-06: it is not
    /// stored). Down gives each date its first moment in UTC+7 and leaves the rest empty. One Open cycle per raft stays as it is.
    /// </summary>
    public partial class CultivationCyclesV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "CultivationCycles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CloseDate",
                table: "CultivationCycles",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE CultivationCycles
                SET StartDate = CAST(SWITCHOFFSET(OpenedAt, '+07:00') AS date),
                    CloseDate = CASE WHEN StatusId = 602 THEN CAST(SWITCHOFFSET(COALESCE(ClosedAt, OpenedAt), '+07:00') AS date) END;
                """);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "StartDate",
                table: "CultivationCycles",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.DropForeignKey(
                name: "FK_CultivationCycles_FarmingInputItems_PlannedSeedTypeId",
                table: "CultivationCycles");

            migrationBuilder.DropIndex(
                name: "IX_CultivationCycles_PlannedSeedTypeId",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "OpenedAt",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "PlannedSeedTypeId",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "SeedingCompletedAt",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "SeedingCompletionReason",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "ClosingSummary",
                table: "CultivationCycles");

            migrationBuilder.AddColumn<string>(
                name: "Assessment",
                table: "CultivationCycles",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CloseRemindedAt",
                table: "CultivationCycles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClosedEarly",
                table: "CultivationCycles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExpectedHarvestDate",
                table: "CultivationCycles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedSeedQuantity",
                table: "CultivationCycles",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SeedingCompletedOn",
                table: "CultivationCycles",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TargetYieldKg",
                table: "CultivationCycles",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 9,
                columns: new[] { "GuardCondition", "UseCases" },
                values: new object[] { "Every seeded lane is Harvested; any other close is an early close the Cooperative Owner approves first (BR-CYCLE-07).", "CYCLE-06" });

            migrationBuilder.CreateIndex(
                name: "IX_CultivationCycles_RaftId_StartDate",
                table: "CultivationCycles",
                columns: new[] { "RaftId", "StartDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CultivationCycles_RaftId_StartDate",
                table: "CultivationCycles");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OpenedAt",
                table: "CultivationCycles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                table: "CultivationCycles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE CultivationCycles
                SET OpenedAt = TODATETIMEOFFSET(CAST(StartDate AS datetime2), '+07:00'),
                    ClosedAt = TODATETIMEOFFSET(CAST(CloseDate AS datetime2), '+07:00');
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "OpenedAt",
                table: "CultivationCycles",
                type: "datetimeoffset",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "datetimeoffset",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "CloseDate",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "ClosedEarly",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "CloseRemindedAt",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "Assessment",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "ExpectedHarvestDate",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "ExpectedSeedQuantity",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "SeedingCompletedOn",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "CultivationCycles");

            migrationBuilder.DropColumn(
                name: "TargetYieldKg",
                table: "CultivationCycles");

            migrationBuilder.AddColumn<string>(
                name: "ClosingSummary",
                table: "CultivationCycles",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SeedingCompletedAt",
                table: "CultivationCycles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlannedSeedTypeId",
                table: "CultivationCycles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeedingCompletionReason",
                table: "CultivationCycles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 9,
                columns: new[] { "GuardCondition", "UseCases" },
                values: new object[] { "The seeded portion has been fully harvested or written off (BR-21).", "UC27" });

            migrationBuilder.CreateIndex(
                name: "IX_CultivationCycles_PlannedSeedTypeId",
                table: "CultivationCycles",
                column: "PlannedSeedTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_CultivationCycles_FarmingInputItems_PlannedSeedTypeId",
                table: "CultivationCycles",
                column: "PlannedSeedTypeId",
                principalTable: "FarmingInputItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
