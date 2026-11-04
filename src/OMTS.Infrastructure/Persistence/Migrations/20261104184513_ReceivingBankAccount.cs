using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReceivingBankAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BankAccount_AcceptedAt",
                table: "Cooperatives",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankAccount_BankBin",
                table: "Cooperatives",
                type: "nvarchar(6)",
                maxLength: 6,
                nullable: true);

            // No service ever wrote the old columns, and an account the Payment Notification Service never accepted and
            // that has no bank BIN is not a receiving bank account (DEBT-01 POST-1): the Owner sets it again.
            migrationBuilder.Sql("UPDATE Cooperatives SET BankAccount_BankName = NULL, BankAccount_AccountNumber = NULL, BankAccount_AccountHolderName = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BankAccount_AcceptedAt",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "BankAccount_BankBin",
                table: "Cooperatives");
        }
    }
}
