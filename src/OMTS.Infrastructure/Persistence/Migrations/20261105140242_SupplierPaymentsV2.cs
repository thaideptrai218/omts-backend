using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupplierPaymentsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierPayments_PurchaseReceipts_ReceiptId",
                table: "SupplierPayments");

            migrationBuilder.DropIndex(
                name: "IX_SupplierPayments_ReceiptId",
                table: "SupplierPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierPayment_Amount_Thousands",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "EvidenceUrls",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "ReceiptId",
                table: "SupplierPayments");

            migrationBuilder.RenameColumn(
                name: "ReferenceCode",
                table: "SupplierPayments",
                newName: "BankReference");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancelledAt",
                table: "SupplierPayments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "SupplierPayments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            // Every payment recorded so far is money paid to the supplier.
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "SupplierPayments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Payment");

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "SupplierPayments",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1801,
                column: "Meaning",
                value: "Counts in the supplier debt; cannot be edited.");

            // The code column is required and unique, so the rows already there get TT codes in payment order, and the TT
            // counter starts after them (none on most databases).
            migrationBuilder.Sql(
                """
                UPDATE p SET p.Code = n.Code
                FROM SupplierPayments p
                JOIN (SELECT Id, N'TT' + FORMAT(ROW_NUMBER() OVER (ORDER BY PaymentDate, CreatedAt, Id), '000000') AS Code
                      FROM SupplierPayments) n ON n.Id = p.Id;
                INSERT INTO DocumentSequences ([Key], LastValue)
                SELECT N'TT', COUNT_BIG(*) FROM SupplierPayments;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_BankReference",
                table: "SupplierPayments",
                column: "BankReference");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_Code",
                table: "SupplierPayments",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy: the old model has no money paid back and only whole thousands. Payments stay detached (no receipt).
            migrationBuilder.Sql(
                """
                DELETE FROM SupplierPayments WHERE Kind = N'PaidBack';
                UPDATE SupplierPayments SET Amount = ROUND(Amount, -3) WHERE Amount % 1000 <> 0;
                DELETE FROM DocumentSequences WHERE [Key] = N'TT';
                """);

            migrationBuilder.DropIndex(
                name: "IX_SupplierPayments_BankReference",
                table: "SupplierPayments");

            migrationBuilder.DropIndex(
                name: "IX_SupplierPayments_Code",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "SupplierPayments");

            migrationBuilder.RenameColumn(
                name: "BankReference",
                table: "SupplierPayments",
                newName: "ReferenceCode");

            migrationBuilder.AddColumn<string>(
                name: "EvidenceUrls",
                table: "SupplierPayments",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "ReceiptId",
                table: "SupplierPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1801,
                column: "Meaning",
                value: "Money paid to the supplier; reduces supplier debt.");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_ReceiptId",
                table: "SupplierPayments",
                column: "ReceiptId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierPayment_Amount_Thousands",
                table: "SupplierPayments",
                sql: "[Amount] % 1000 = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierPayments_PurchaseReceipts_ReceiptId",
                table: "SupplierPayments",
                column: "ReceiptId",
                principalTable: "PurchaseReceipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
