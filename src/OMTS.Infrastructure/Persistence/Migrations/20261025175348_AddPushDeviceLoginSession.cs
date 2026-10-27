using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPushDeviceLoginSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LoginSessionId",
                table: "PushDevices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_LoginSessionId",
                table: "PushDevices",
                column: "LoginSessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_PushDevices_LoginSessions_LoginSessionId",
                table: "PushDevices",
                column: "LoginSessionId",
                principalTable: "LoginSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PushDevices_LoginSessions_LoginSessionId",
                table: "PushDevices");

            migrationBuilder.DropIndex(
                name: "IX_PushDevices_LoginSessionId",
                table: "PushDevices");

            migrationBuilder.DropColumn(
                name: "LoginSessionId",
                table: "PushDevices");
        }
    }
}
