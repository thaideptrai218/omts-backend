using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HarvestPlanLineWorkAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "HarvestPlanLineId",
                table: "WorkAssignments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkAssignments_HarvestPlanLineId",
                table: "WorkAssignments",
                column: "HarvestPlanLineId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkAssignments_HarvestPlanLines_HarvestPlanLineId",
                table: "WorkAssignments",
                column: "HarvestPlanLineId",
                principalTable: "HarvestPlanLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkAssignments_HarvestPlanLines_HarvestPlanLineId",
                table: "WorkAssignments");

            migrationBuilder.DropIndex(
                name: "IX_WorkAssignments_HarvestPlanLineId",
                table: "WorkAssignments");

            migrationBuilder.DropColumn(
                name: "HarvestPlanLineId",
                table: "WorkAssignments");
        }
    }
}
