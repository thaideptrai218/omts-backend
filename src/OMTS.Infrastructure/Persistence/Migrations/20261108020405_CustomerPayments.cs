using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_StatusId",
                table: "CustomerPayments");

            migrationBuilder.AddColumn<string>(
                name: "BankReference",
                table: "CustomerPayments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "CustomerPayments",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "CustomerPayments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByAccountId",
                table: "CustomerPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAt",
                table: "CustomerPayments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConfirmedByAccountId",
                table: "CustomerPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeduplicationKey",
                table: "CustomerPayments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "CustomerPayments",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_BankReference",
                table: "CustomerPayments",
                column: "BankReference");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_CancelledByAccountId",
                table: "CustomerPayments",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_ConfirmedByAccountId",
                table: "CustomerPayments",
                column: "ConfirmedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_DeduplicationKey",
                table: "CustomerPayments",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_StatusId_PaymentDate",
                table: "CustomerPayments",
                columns: new[] { "StatusId", "PaymentDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_Accounts_CancelledByAccountId",
                table: "CustomerPayments",
                column: "CancelledByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_Accounts_ConfirmedByAccountId",
                table: "CustomerPayments",
                column: "ConfirmedByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_Accounts_CancelledByAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_Accounts_ConfirmedByAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_BankReference",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_CancelledByAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_ConfirmedByAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_DeduplicationKey",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_StatusId_PaymentDate",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "BankReference",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "CancelledByAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "ConfirmedByAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "DeduplicationKey",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "CustomerPayments");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_StatusId",
                table: "CustomerPayments",
                column: "StatusId");
        }
    }
}
