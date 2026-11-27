using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CultivationLogRaftWithoutCycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The raft is added empty, taken from the cycle every existing log belongs to, and only then made required.
            migrationBuilder.AddColumn<Guid>(
                name: "RaftId",
                table: "CultivationLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE l SET l.RaftId = c.RaftId FROM CultivationLogs AS l INNER JOIN CultivationCycles AS c ON c.Id = l.CycleId");

            migrationBuilder.AlterColumn<Guid>(
                name: "RaftId",
                table: "CultivationLogs",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "CycleId",
                table: "CultivationLogs",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_CultivationLogs_RaftId_VisitDate",
                table: "CultivationLogs",
                columns: new[] { "RaftId", "VisitDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_CultivationLogs_Rafts_RaftId",
                table: "CultivationLogs",
                column: "RaftId",
                principalTable: "Rafts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CultivationLogs_Rafts_RaftId",
                table: "CultivationLogs");

            migrationBuilder.DropIndex(
                name: "IX_CultivationLogs_RaftId_VisitDate",
                table: "CultivationLogs");

            migrationBuilder.DropColumn(
                name: "RaftId",
                table: "CultivationLogs");

            migrationBuilder.AlterColumn<Guid>(
                name: "CycleId",
                table: "CultivationLogs",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
