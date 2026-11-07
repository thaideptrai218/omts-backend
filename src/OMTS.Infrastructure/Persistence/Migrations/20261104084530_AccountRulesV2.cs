using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountRulesV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_Email",
                table: "Accounts");

            migrationBuilder.AddColumn<string>(
                name: "SendTimes",
                table: "VerificationCodes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            // The index now covers unverified emails too (BR-ACCOUNT-02). Of the accounts that share an email, the one
            // with it verified keeps it, otherwise the oldest; the others lose it, as nothing proved it was theirs.
            migrationBuilder.Sql(
                """
                WITH Ranked AS (
                    SELECT Email, ROW_NUMBER() OVER (
                        PARTITION BY Email
                        ORDER BY CASE WHEN EmailVerifiedAt IS NULL THEN 1 ELSE 0 END, CreatedAt, Id) AS Position
                    FROM Accounts
                    WHERE Email IS NOT NULL)
                UPDATE Ranked SET Email = NULL WHERE Position > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Email",
                table: "Accounts",
                column: "Email",
                unique: true,
                filter: "[Email] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_Email",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SendTimes",
                table: "VerificationCodes");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Email",
                table: "Accounts",
                column: "Email",
                unique: true,
                filter: "[Email] IS NOT NULL AND [EmailVerifiedAt] IS NOT NULL");
        }
    }
}
