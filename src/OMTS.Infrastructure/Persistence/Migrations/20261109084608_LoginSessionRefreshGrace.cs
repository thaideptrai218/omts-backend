using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoginSessionRefreshGrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreviousRefreshTokenGraceEndsAt",
                table: "LoginSessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousRefreshTokenHash",
                table: "LoginSessions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginSessions_PreviousRefreshTokenHash",
                table: "LoginSessions",
                column: "PreviousRefreshTokenHash",
                filter: "[PreviousRefreshTokenHash] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LoginSessions_PreviousRefreshTokenHash",
                table: "LoginSessions");

            migrationBuilder.DropColumn(
                name: "PreviousRefreshTokenGraceEndsAt",
                table: "LoginSessions");

            migrationBuilder.DropColumn(
                name: "PreviousRefreshTokenHash",
                table: "LoginSessions");
        }
    }
}
