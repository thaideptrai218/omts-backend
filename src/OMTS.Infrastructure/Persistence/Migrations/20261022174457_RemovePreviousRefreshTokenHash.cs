using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemovePreviousRefreshTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LoginSessions_PreviousRefreshTokenHash",
                table: "LoginSessions");

            migrationBuilder.DropColumn(
                name: "PreviousRefreshTokenHash",
                table: "LoginSessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousRefreshTokenHash",
                table: "LoginSessions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginSessions_PreviousRefreshTokenHash",
                table: "LoginSessions",
                column: "PreviousRefreshTokenHash");
        }
    }
}
