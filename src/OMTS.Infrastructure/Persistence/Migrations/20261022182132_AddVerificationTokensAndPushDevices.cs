using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationTokensAndPushDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VerificationCodes_AccountId",
                table: "VerificationCodes");

            migrationBuilder.AlterColumn<string>(
                name: "CodeHash",
                table: "VerificationCodes",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "VerificationCodes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TokenConsumedAt",
                table: "VerificationCodes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TokenExpiresAt",
                table: "VerificationCodes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "VerificationCodes",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PushDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PermissionGranted = table.Column<bool>(type: "bit", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushDevices_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_AccountId_Purpose",
                table: "VerificationCodes",
                columns: new[] { "AccountId", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_TokenHash",
                table: "VerificationCodes",
                column: "TokenHash",
                unique: true,
                filter: "[TokenHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_AccountId",
                table: "PushDevices",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_Token",
                table: "PushDevices",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushDevices");

            migrationBuilder.DropIndex(
                name: "IX_VerificationCodes_AccountId_Purpose",
                table: "VerificationCodes");

            migrationBuilder.DropIndex(
                name: "IX_VerificationCodes_TokenHash",
                table: "VerificationCodes");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "VerificationCodes");

            migrationBuilder.DropColumn(
                name: "TokenConsumedAt",
                table: "VerificationCodes");

            migrationBuilder.DropColumn(
                name: "TokenExpiresAt",
                table: "VerificationCodes");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "VerificationCodes");

            migrationBuilder.AlterColumn<string>(
                name: "CodeHash",
                table: "VerificationCodes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_AccountId",
                table: "VerificationCodes",
                column: "AccountId");
        }
    }
}
