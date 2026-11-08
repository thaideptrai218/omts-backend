using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VietQrPendingSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TransferInvoiceCode",
                table: "CustomerPayments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TransferredAt",
                table: "CustomerPayments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingSales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    CashAmount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ToPayAmount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    SaleJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingSales", x => x.Id);
                    table.CheckConstraint("CK_PendingSale_Amounts", "[Total] >= 0 AND [CashAmount] >= 0 AND [ToPayAmount] > 0 AND [CashAmount] + [ToPayAmount] = [Total]");
                    table.ForeignKey(
                        name: "FK_PendingSales_Accounts_CreatedByAccountId",
                        column: x => x.CreatedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingSales_Accounts_EndedByAccountId",
                        column: x => x.EndedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingSales_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingSales_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingSales_SalesInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_TransferInvoiceCode",
                table: "CustomerPayments",
                column: "TransferInvoiceCode");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_VietQrBankReference",
                table: "CustomerPayments",
                column: "BankReference",
                unique: true,
                filter: "[Method] = 'VietQrInvoice' AND [BankReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PendingSales_Code",
                table: "PendingSales",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingSales_CreatedByAccountId",
                table: "PendingSales",
                column: "CreatedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingSales_CustomerId",
                table: "PendingSales",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingSales_DeduplicationKey",
                table: "PendingSales",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PendingSales_EndedByAccountId",
                table: "PendingSales",
                column: "EndedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingSales_InvoiceId",
                table: "PendingSales",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingSales_Status_ZoneId",
                table: "PendingSales",
                columns: new[] { "Status", "ZoneId" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingSales_ZoneId",
                table: "PendingSales",
                column: "ZoneId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingSales");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_TransferInvoiceCode",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_VietQrBankReference",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "TransferInvoiceCode",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "TransferredAt",
                table: "CustomerPayments");
        }
    }
}
