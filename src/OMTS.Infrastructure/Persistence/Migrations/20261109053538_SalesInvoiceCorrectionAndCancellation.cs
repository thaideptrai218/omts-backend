using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesInvoiceCorrectionAndCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "SalesInvoices",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "SalesInvoices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancelledByAccountId",
                table: "SalesInvoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCorrectedAt",
                table: "SalesInvoices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCorrectionKind",
                table: "SalesInvoices",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCorrectionReason",
                table: "SalesInvoices",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_CancelledByAccountId",
                table: "SalesInvoices",
                column: "CancelledByAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoices_Accounts_CancelledByAccountId",
                table: "SalesInvoices",
                column: "CancelledByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoices_Accounts_CancelledByAccountId",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_CancelledByAccountId",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "CancelledByAccountId",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "LastCorrectedAt",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "LastCorrectionKind",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "LastCorrectionReason",
                table: "SalesInvoices");
        }
    }
}
