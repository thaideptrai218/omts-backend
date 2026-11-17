using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalesReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReturns", x => x.Id);
                    table.CheckConstraint("CK_SalesReturn_Cancelled_Has_Reason", "[StatusId] <> 4102 OR ([CancelReason] IS NOT NULL AND [CancelledAt] IS NOT NULL AND [CancelledByAccountId] IS NOT NULL)");
                    table.CheckConstraint("CK_SalesReturn_Value_NotNegative", "[Value] >= 0");
                    table.ForeignKey(
                        name: "FK_SalesReturns_Accounts_CancelledByAccountId",
                        column: x => x.CancelledByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReturns_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReturns_SalesInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReturns_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesReturnLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HarvestLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DiscardedKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReturnLines", x => x.Id);
                    table.CheckConstraint("CK_SalesReturnLine_Discarded_Matches_Outcome", "([Outcome] = 'ThrownAway' AND [DiscardedKg] = [ReturnedKg]) OR ([Outcome] = 'BackForSale' AND [DiscardedKg] = 0)");
                    table.CheckConstraint("CK_SalesReturnLine_ReturnedKg_Positive", "[ReturnedKg] > 0");
                    table.ForeignKey(
                        name: "FK_SalesReturnLines_HarvestLots_HarvestLotId",
                        column: x => x.HarvestLotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReturnLines_SalesReturns_ReturnId",
                        column: x => x.ReturnId,
                        principalTable: "SalesReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 4101, "RECORDED", null, "Recorded", "SALES_RETURN", false, true, "Takes the invoice out of the customer debt and counts in the lots and the reports; cannot be edited.", 1 },
                    { 4102, "CANCELLED", null, "Cancelled", "SALES_RETURN", true, false, "Recorded by mistake; counts nowhere.", 2 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 240, "Cooperative Owner", 4101, "A return recorded by mistake; it no longer counts in the customer debt, the lots or the reports, and the invoice can again be corrected or cancelled.", true, 4102, "Action", "CLAIM-03" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLines_HarvestLotId",
                table: "SalesReturnLines",
                column: "HarvestLotId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturnLines_ReturnId_InvoiceLineId",
                table: "SalesReturnLines",
                columns: new[] { "ReturnId", "InvoiceLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_CancelledByAccountId",
                table: "SalesReturns",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_Code",
                table: "SalesReturns",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_InvoiceId",
                table: "SalesReturns",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_RecordedByAccountId",
                table: "SalesReturns",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_StatusId",
                table: "SalesReturns",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "UX_SalesReturns_Invoice_Recorded",
                table: "SalesReturns",
                column: "InvoiceId",
                unique: true,
                filter: "[StatusId] = 4101");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalesReturnLines");

            migrationBuilder.DropTable(
                name: "SalesReturns");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 240);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4101);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4102);
        }
    }
}
