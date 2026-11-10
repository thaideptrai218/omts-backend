using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseReturnTimeAndSupplierPaymentAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidAt",
                table: "SupplierPayments",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "PaidByAccountId",
                table: "SupplierPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReturnedAt",
                table: "PurchaseReturns",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // An older payment was paid at the start of its date in Vietnam, by whoever recorded it. An older return went back
            // when it was saved if that was on its date, otherwise at the start of its date.
            migrationBuilder.Sql(@"
UPDATE SupplierPayments
SET PaidAt = TODATETIMEOFFSET(CAST(PaymentDate AS datetime2), '+07:00'),
    PaidByAccountId = RecordedByAccountId;

UPDATE PurchaseReturns
SET ReturnedAt = CASE
    WHEN CAST(SWITCHOFFSET(CreatedAt, '+07:00') AS date) = ReturnDate THEN SWITCHOFFSET(CreatedAt, '+07:00')
    ELSE TODATETIMEOFFSET(CAST(ReturnDate AS datetime2), '+07:00')
END;
");

            migrationBuilder.CreateTable(
                name: "SupplierPaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPaymentAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierPaymentAllocations_PurchaseReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "PurchaseReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPaymentAllocations_SupplierPayments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "SupplierPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_PaidByAccountId",
                table: "SupplierPayments",
                column: "PaidByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentAllocations_PaymentId_ReceiptId",
                table: "SupplierPaymentAllocations",
                columns: new[] { "PaymentId", "ReceiptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPaymentAllocations_ReceiptId",
                table: "SupplierPaymentAllocations",
                column: "ReceiptId");

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierPayments_Accounts_PaidByAccountId",
                table: "SupplierPayments",
                column: "PaidByAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierPayments_Accounts_PaidByAccountId",
                table: "SupplierPayments");

            migrationBuilder.DropTable(
                name: "SupplierPaymentAllocations");

            migrationBuilder.DropIndex(
                name: "IX_SupplierPayments_PaidByAccountId",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "PaidByAccountId",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "ReturnedAt",
                table: "PurchaseReturns");
        }
    }
}
