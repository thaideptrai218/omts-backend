using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalesInvoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InvoicedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PurchaseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TakesRequestInFull = table.Column<bool>(type: "bit", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    DiscountDecidedByOwner = table.Column<bool>(type: "bit", nullable: false),
                    OtherCharges = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PriceCutTotal = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PaymentTerm = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesInvoices", x => x.Id);
                    table.CheckConstraint("CK_SalesInvoice_Amounts", "[DiscountAmount] >= 0 AND [OtherCharges] >= 0 AND [Subtotal] >= 0 AND [PriceCutTotal] >= 0");
                    table.CheckConstraint("CK_SalesInvoice_PayLater_Has_Customer", "[PaymentTerm] <> 'PayLater' OR [CustomerId] IS NOT NULL");
                    table.CheckConstraint("CK_SalesInvoice_Request_Has_Customer", "[PurchaseRequestId] IS NULL OR [CustomerId] IS NOT NULL");
                    table.CheckConstraint("CK_SalesInvoice_Total", "[Total] >= 0 AND [Total] = [Subtotal] - [DiscountAmount] + [OtherCharges] - [PriceCutTotal]");
                    table.ForeignKey(
                        name: "FK_SalesInvoices_Accounts_CreatedByAccountId",
                        column: x => x.CreatedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesInvoices_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesInvoices_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesInvoices_PurchaseRequests_PurchaseRequestId",
                        column: x => x.PurchaseRequestId,
                        principalTable: "PurchaseRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesInvoices_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CollectedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPayments", x => x.Id);
                    table.CheckConstraint("CK_CustomerPayment_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Accounts_CollectedByAccountId",
                        column: x => x.CollectedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_SalesInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesInvoiceLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HarvestLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ListPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PriceSource = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    SoldWholeLot = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesInvoiceLines", x => x.Id);
                    table.CheckConstraint("CK_SalesInvoiceLine_Kg", "[Kg] > 0");
                    table.CheckConstraint("CK_SalesInvoiceLine_Prices", "[UnitPrice] > 0 AND [ListPrice] > 0 AND [Amount] >= 0");
                    table.ForeignKey(
                        name: "FK_SalesInvoiceLines_HarvestLots_HarvestLotId",
                        column: x => x.HarvestLotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesInvoiceLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesInvoiceLines_SalesInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesInvoicePriceCuts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    CutDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    EnteredByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesInvoicePriceCuts", x => x.Id);
                    table.CheckConstraint("CK_SalesInvoicePriceCut_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_SalesInvoicePriceCuts_Accounts_EnteredByAccountId",
                        column: x => x.EnteredByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesInvoicePriceCuts_SalesInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 3501, "COMPLETED", null, "Completed", "SALES_INVOICE", false, true, "The sale is saved; only the Cooperative Owner corrects or cancels it.", 1 },
                    { 3502, "CANCELLED", null, "Cancelled", "SALES_INVOICE", true, false, "The sale did not happen; kept with its reason and counts nowhere.", 2 },
                    { 3601, "AWAITING_CONFIRMATION", null, "Awaiting confirmation", "CUSTOMER_PAYMENT", false, true, "Recorded at the port or by the system; counts in the debt until the Accountant confirms or cancels it.", 1 },
                    { 3602, "CONFIRMED", null, "Confirmed", "CUSTOMER_PAYMENT", false, false, "Checked or recorded by the Accountant.", 2 },
                    { 3603, "CANCELLED", null, "Cancelled", "CUSTOMER_PAYMENT", true, false, "Recorded by mistake or never received; no longer counts.", 3 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 200, "Cooperative Owner", 3501, "The invoice has no sales return that is not cancelled; a cash payment is cancelled with it only when the cash was given back.", true, 3502, "Action", "SALE-08" },
                    { 210, "Accountant", 3601, "The cash or the transfer that arrived matches the payment.", false, 3602, "Action", "DEBT-02" },
                    { 211, "Accountant; Cooperative Owner", 3601, "The customer did not pay, or the cash was given back when the invoice was cancelled.", true, 3603, "Action", "DEBT-02, SALE-08" },
                    { 212, "Accountant", 3602, "Recorded by mistake, or the money never arrived.", true, 3603, "Action", "DEBT-02" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_ClosedBySalesInvoiceId",
                table: "HarvestLots",
                column: "ClosedBySalesInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_Code",
                table: "CustomerPayments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_CollectedByAccountId",
                table: "CustomerPayments",
                column: "CollectedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_CustomerId_StatusId",
                table: "CustomerPayments",
                columns: new[] { "CustomerId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_InvoiceId",
                table: "CustomerPayments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_RecordedByAccountId",
                table: "CustomerPayments",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_StatusId",
                table: "CustomerPayments",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoiceLines_HarvestLotId",
                table: "SalesInvoiceLines",
                column: "HarvestLotId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoiceLines_InvoiceId_LineNumber",
                table: "SalesInvoiceLines",
                columns: new[] { "InvoiceId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoiceLines_ProductId",
                table: "SalesInvoiceLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoicePriceCuts_EnteredByAccountId",
                table: "SalesInvoicePriceCuts",
                column: "EnteredByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoicePriceCuts_InvoiceId_CutDate",
                table: "SalesInvoicePriceCuts",
                columns: new[] { "InvoiceId", "CutDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_Code",
                table: "SalesInvoices",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_CreatedByAccountId",
                table: "SalesInvoices",
                column: "CreatedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_CustomerId_StatusId",
                table: "SalesInvoices",
                columns: new[] { "CustomerId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_DeduplicationKey",
                table: "SalesInvoices",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_InvoiceDate_ZoneId",
                table: "SalesInvoices",
                columns: new[] { "InvoiceDate", "ZoneId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_PurchaseRequestId",
                table: "SalesInvoices",
                column: "PurchaseRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_StatusId",
                table: "SalesInvoices",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_ZoneId",
                table: "SalesInvoices",
                column: "ZoneId");

            // No invoice existed before this migration, so no lot can name one: a value left there (by hand, or by a Down) would stop the key.
            migrationBuilder.Sql("UPDATE [HarvestLots] SET [ClosedBySalesInvoiceId] = NULL WHERE [ClosedBySalesInvoiceId] IS NOT NULL;");

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestLots_SalesInvoices_ClosedBySalesInvoiceId",
                table: "HarvestLots",
                column: "ClosedBySalesInvoiceId",
                principalTable: "SalesInvoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HarvestLots_SalesInvoices_ClosedBySalesInvoiceId",
                table: "HarvestLots");

            // The invoices go: a lot they closed stays Closed (Sold whole lot) but names none, as before this migration.
            migrationBuilder.Sql("UPDATE [HarvestLots] SET [ClosedBySalesInvoiceId] = NULL WHERE [ClosedBySalesInvoiceId] IS NOT NULL;");

            migrationBuilder.DropTable(
                name: "CustomerPayments");

            migrationBuilder.DropTable(
                name: "SalesInvoiceLines");

            migrationBuilder.DropTable(
                name: "SalesInvoicePriceCuts");

            migrationBuilder.DropTable(
                name: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLots_ClosedBySalesInvoiceId",
                table: "HarvestLots");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 200);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 210);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 211);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 212);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3501);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3502);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3601);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3602);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3603);
        }
    }
}
