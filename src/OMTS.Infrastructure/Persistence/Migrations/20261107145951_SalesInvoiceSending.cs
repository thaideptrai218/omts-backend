using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesInvoiceSending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QrCreatedAt",
                table: "SalesInvoices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QrDecidedAt",
                table: "SalesInvoices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QrToken",
                table: "SalesInvoices",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RecipientAccountId",
                table: "Notifications",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_QrToken",
                table: "SalesInvoices",
                column: "QrToken",
                unique: true,
                filter: "[QrToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_QrToken",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "QrCreatedAt",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "QrDecidedAt",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "QrToken",
                table: "SalesInvoices");

            // Sendings to profiles without a trader account have no recipient, which the older schema cannot hold.
            migrationBuilder.Sql("DELETE FROM [Notifications] WHERE [RecipientAccountId] IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "RecipientAccountId",
                table: "Notifications",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
