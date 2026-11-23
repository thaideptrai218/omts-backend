using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTraderCheckouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TraderCheckouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PayOsOrderCode = table.Column<long>(type: "bigint", nullable: false),
                    PayOsPaymentLinkId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    QrContent = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    BankBin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AccountNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AccountName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TransferContent = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ZaloOrderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ZaloOrderLinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ZaloReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ZaloReportAttempts = table.Column<int>(type: "int", nullable: false),
                    ZaloLastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ZaloLastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraderCheckouts", x => x.Id);
                    table.CheckConstraint("CK_TraderCheckout_Amount", "[Amount] > 0 AND [ZaloReportAttempts] >= 0");
                    table.ForeignKey(
                        name: "FK_TraderCheckouts_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraderCheckouts_CustomerPayments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "CustomerPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraderCheckouts_SalesInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TraderCheckouts_AccountId",
                table: "TraderCheckouts",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TraderCheckouts_InvoiceId_CreatedAt",
                table: "TraderCheckouts",
                columns: new[] { "InvoiceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TraderCheckouts_PaymentId",
                table: "TraderCheckouts",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraderCheckouts_PayOsOrderCode",
                table: "TraderCheckouts",
                column: "PayOsOrderCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TraderCheckouts_ZaloReportedAt_ZaloOrderId",
                table: "TraderCheckouts",
                columns: new[] { "ZaloReportedAt", "ZaloOrderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TraderCheckouts");
        }
    }
}
