using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NurserySeedingResponsiblePerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ResponsibleAccountId",
                table: "SeedingRecords",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResponsibleAccountId",
                table: "NurseryBatches",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeedingRecords_ResponsibleAccountId",
                table: "SeedingRecords",
                column: "ResponsibleAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_NurseryBatches_ResponsibleAccountId",
                table: "NurseryBatches",
                column: "ResponsibleAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_NurseryBatches_Accounts_ResponsibleAccountId",
                table: "NurseryBatches",
                column: "ResponsibleAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeedingRecords_Accounts_ResponsibleAccountId",
                table: "SeedingRecords",
                column: "ResponsibleAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NurseryBatches_Accounts_ResponsibleAccountId",
                table: "NurseryBatches");

            migrationBuilder.DropForeignKey(
                name: "FK_SeedingRecords_Accounts_ResponsibleAccountId",
                table: "SeedingRecords");

            migrationBuilder.DropIndex(
                name: "IX_SeedingRecords_ResponsibleAccountId",
                table: "SeedingRecords");

            migrationBuilder.DropIndex(
                name: "IX_NurseryBatches_ResponsibleAccountId",
                table: "NurseryBatches");

            migrationBuilder.DropColumn(
                name: "ResponsibleAccountId",
                table: "SeedingRecords");

            migrationBuilder.DropColumn(
                name: "ResponsibleAccountId",
                table: "NurseryBatches");
        }
    }
}
