using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoginSessionDeviceAndSignInMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LoginSessions_AccountId",
                table: "LoginSessions");

            migrationBuilder.AddColumn<string>(
                name: "AuthMethod",
                table: "LoginSessions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Password");

            migrationBuilder.AddColumn<string>(
                name: "DeviceKey",
                table: "LoginSessions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokeCause",
                table: "LoginSessions",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginSessions_AccountId_DeviceKey",
                table: "LoginSessions",
                columns: new[] { "AccountId", "DeviceKey" });

            // Sessions that already exist: the Mini App is only ever entered through Zalo, and a revoked session is
            // a plain revocation unless the revoke reason says the refresh token was replayed.
            migrationBuilder.Sql("UPDATE [LoginSessions] SET [AuthMethod] = 'ZaloMiniApp' WHERE [ClientType] = 'Mobile'");
            migrationBuilder.Sql("UPDATE [LoginSessions] SET [RevokeCause] = 'Revoked' WHERE [RevokedAt] IS NOT NULL");
            migrationBuilder.Sql("UPDATE [LoginSessions] SET [RevokeCause] = 'TokenReuse' WHERE [RevokeReason] = 'Refresh token reuse detected'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LoginSessions_AccountId_DeviceKey",
                table: "LoginSessions");

            migrationBuilder.DropColumn(
                name: "AuthMethod",
                table: "LoginSessions");

            migrationBuilder.DropColumn(
                name: "DeviceKey",
                table: "LoginSessions");

            migrationBuilder.DropColumn(
                name: "RevokeCause",
                table: "LoginSessions");

            migrationBuilder.CreateIndex(
                name: "IX_LoginSessions_AccountId",
                table: "LoginSessions",
                column: "AccountId");
        }
    }
}
