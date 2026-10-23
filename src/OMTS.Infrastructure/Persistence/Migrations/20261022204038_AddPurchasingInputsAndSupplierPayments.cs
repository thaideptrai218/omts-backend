using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchasingInputsAndSupplierPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CultivationCycles_OysterSeedTypes_PlannedSeedTypeId",
                table: "CultivationCycles");

            migrationBuilder.DropForeignKey(
                name: "FK_SeedReceiptLots_OysterSeedTypes_SeedTypeId",
                table: "SeedReceiptLots");

            migrationBuilder.DropForeignKey(
                name: "FK_SeedReceiptLots_SeedSuppliers_SupplierId",
                table: "SeedReceiptLots");

            migrationBuilder.DropTable(
                name: "OysterSeedTypes");

            migrationBuilder.DropTable(
                name: "RaftMaterialLines");

            migrationBuilder.DropTable(
                name: "SeedSuppliers");

            migrationBuilder.DropTable(
                name: "RaftMaterialRecords");

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseReceiptLineId",
                table: "SeedReceiptLots",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "FarmingInputItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Group = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IllustrationUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FarmingInputItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FarmingInputItems_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SupplierType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OtherContacts = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CertificateUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Suppliers_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceiptDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    AmountPaidNow = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PaidNowMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    InvoicePhotoUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CopiedFromReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReceipts", x => x.Id);
                    table.CheckConstraint("CK_PurchaseReceipt_AmountPaidNow_Thousands", "[AmountPaidNow] % 1000 = 0");
                    table.CheckConstraint("CK_PurchaseReceipt_DiscountAmount_Thousands", "[DiscountAmount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_PurchaseReceipts_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReceipts_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReceipts_PurchaseReceipts_CopiedFromReceiptId",
                        column: x => x.CopiedFromReceiptId,
                        principalTable: "PurchaseReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReceipts_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReceipts_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReceipts_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseReceiptLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    LineAmount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    SupplierLotCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ActualSource = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReceiptLines", x => x.Id);
                    table.CheckConstraint("CK_PurchaseReceiptLine_LineAmount_Thousands", "[LineAmount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_PurchaseReceiptLines_FarmingInputItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "FarmingInputItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReceiptLines_PurchaseReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "PurchaseReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RefundAmount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    RefundMethod = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DebtReductionAmount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReturns", x => x.Id);
                    table.CheckConstraint("CK_PurchaseReturn_DebtReductionAmount_Thousands", "[DebtReductionAmount] % 1000 = 0");
                    table.CheckConstraint("CK_PurchaseReturn_RefundAmount_Thousands", "[RefundAmount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_PurchaseReturns_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReturns_PurchaseReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "PurchaseReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReferenceCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPayments", x => x.Id);
                    table.CheckConstraint("CK_SupplierPayment_Amount_Thousands", "[Amount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_SupplierPayments_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_PurchaseReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "PurchaseReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierPayments_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseReturnLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ReturnValue = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReturnLines", x => x.Id);
                    table.CheckConstraint("CK_PurchaseReturnLine_ReturnValue_Thousands", "[ReturnValue] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_PurchaseReturnLines_PurchaseReceiptLines_ReceiptLineId",
                        column: x => x.ReceiptLineId,
                        principalTable: "PurchaseReceiptLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReturnLines_PurchaseReturns_ReturnId",
                        column: x => x.ReturnId,
                        principalTable: "PurchaseReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 1701, "DRAFT", null, "Draft", "PURCHASE_RECEIPT", false, true, "Being recorded; does not count in costs, supplier debt or seed lots.", 1 },
                    { 1702, "COMPLETED", null, "Completed", "PURCHASE_RECEIPT", false, false, "Counts in input costs and supplier debt; seed lots created; never edited.", 2 },
                    { 1703, "CANCELLED", null, "Cancelled", "PURCHASE_RECEIPT", true, false, "Recorded by mistake; leaves costs and supplier debt, its seed lots cannot be stocked.", 3 },
                    { 1801, "RECORDED", null, "Recorded", "SUPPLIER_PAYMENT", false, true, "Money paid to the supplier; reduces supplier debt.", 1 },
                    { 1802, "CANCELLED", null, "Cancelled", "SUPPLIER_PAYMENT", true, false, "Cancelled together with its purchase receipt.", 2 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 70, "Cooperative Owner; Zone Manager", 1701, "Full validation of supplier, zone, raft, lines and amounts passes.", false, 1702, "Action", "INP-03" },
                    { 71, "Cooperative Owner; Zone Manager", 1701, "Cancel a draft that will not be completed.", true, 1703, "Action", "INP-04" },
                    { 72, "Cooperative Owner; Zone Manager", 1702, "No seed lot of the receipt is stocked and the receipt has no purchase return.", true, 1703, "Action", "INP-04" },
                    { 73, "Cooperative Owner; Zone Manager", 1801, "The receipt is cancelled together with its payments.", true, 1802, "Action", "INP-04" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeedReceiptLots_PurchaseReceiptLineId",
                table: "SeedReceiptLots",
                column: "PurchaseReceiptLineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FarmingInputItems_Code",
                table: "FarmingInputItems",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FarmingInputItems_StatusId",
                table: "FarmingInputItems",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceiptLines_ItemId",
                table: "PurchaseReceiptLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceiptLines_ReceiptId",
                table: "PurchaseReceiptLines",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_Code",
                table: "PurchaseReceipts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_CopiedFromReceiptId",
                table: "PurchaseReceipts",
                column: "CopiedFromReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_RaftId",
                table: "PurchaseReceipts",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_RecordedByAccountId",
                table: "PurchaseReceipts",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_StatusId",
                table: "PurchaseReceipts",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_SupplierId_StatusId",
                table: "PurchaseReceipts",
                columns: new[] { "SupplierId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_ZoneId_ReceiptDate",
                table: "PurchaseReceipts",
                columns: new[] { "ZoneId", "ReceiptDate" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_ReceiptLineId",
                table: "PurchaseReturnLines",
                column: "ReceiptLineId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_ReturnId",
                table: "PurchaseReturnLines",
                column: "ReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_Code",
                table: "PurchaseReturns",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_ReceiptId",
                table: "PurchaseReturns",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_RecordedByAccountId",
                table: "PurchaseReturns",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_DeduplicationKey",
                table: "SupplierPayments",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_ReceiptId",
                table: "SupplierPayments",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_RecordedByAccountId",
                table: "SupplierPayments",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_StatusId",
                table: "SupplierPayments",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_SupplierId_PaymentDate",
                table: "SupplierPayments",
                columns: new[] { "SupplierId", "PaymentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_PhoneNumber",
                table: "Suppliers",
                column: "PhoneNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_StatusId",
                table: "Suppliers",
                column: "StatusId");

            migrationBuilder.AddForeignKey(
                name: "FK_CultivationCycles_FarmingInputItems_PlannedSeedTypeId",
                table: "CultivationCycles",
                column: "PlannedSeedTypeId",
                principalTable: "FarmingInputItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeedReceiptLots_FarmingInputItems_SeedTypeId",
                table: "SeedReceiptLots",
                column: "SeedTypeId",
                principalTable: "FarmingInputItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeedReceiptLots_PurchaseReceiptLines_PurchaseReceiptLineId",
                table: "SeedReceiptLots",
                column: "PurchaseReceiptLineId",
                principalTable: "PurchaseReceiptLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeedReceiptLots_Suppliers_SupplierId",
                table: "SeedReceiptLots",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CultivationCycles_FarmingInputItems_PlannedSeedTypeId",
                table: "CultivationCycles");

            migrationBuilder.DropForeignKey(
                name: "FK_SeedReceiptLots_FarmingInputItems_SeedTypeId",
                table: "SeedReceiptLots");

            migrationBuilder.DropForeignKey(
                name: "FK_SeedReceiptLots_PurchaseReceiptLines_PurchaseReceiptLineId",
                table: "SeedReceiptLots");

            migrationBuilder.DropForeignKey(
                name: "FK_SeedReceiptLots_Suppliers_SupplierId",
                table: "SeedReceiptLots");

            migrationBuilder.DropTable(
                name: "PurchaseReturnLines");

            migrationBuilder.DropTable(
                name: "SupplierPayments");

            migrationBuilder.DropTable(
                name: "PurchaseReceiptLines");

            migrationBuilder.DropTable(
                name: "PurchaseReturns");

            migrationBuilder.DropTable(
                name: "FarmingInputItems");

            migrationBuilder.DropTable(
                name: "PurchaseReceipts");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_SeedReceiptLots_PurchaseReceiptLineId",
                table: "SeedReceiptLots");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 70);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 71);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 72);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 73);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1701);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1702);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1703);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1801);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1802);

            migrationBuilder.DropColumn(
                name: "PurchaseReceiptLineId",
                table: "SeedReceiptLots");

            migrationBuilder.CreateTable(
                name: "OysterSeedTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IllustrationUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OysterSeedTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OysterSeedTypes_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaftMaterialRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoicePhotoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PurchasePlaceNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RecordDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReplacementReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaftMaterialRecords", x => x.Id);
                    table.CheckConstraint("CK_RaftMaterialRecord_TotalAmount_Thousands", "[TotalAmount] % 1000 = 0");
                    table.ForeignKey(
                        name: "FK_RaftMaterialRecords_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RaftMaterialRecords_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeedSuppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OtherContacts = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeedSuppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeedSuppliers_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaftMaterialLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaterialType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Position = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Specification = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaftMaterialLines", x => x.Id);
                    table.CheckConstraint("CK_RaftMaterialLine_LineAmount_Thousands", "[LineAmount] % 1000 = 0");
                    table.CheckConstraint("CK_RaftMaterialLine_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_RaftMaterialLine_UnitPrice", "[UnitPrice] >= 0");
                    table.ForeignKey(
                        name: "FK_RaftMaterialLines_RaftMaterialRecords_RecordId",
                        column: x => x.RecordId,
                        principalTable: "RaftMaterialRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OysterSeedTypes_Code",
                table: "OysterSeedTypes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OysterSeedTypes_StatusId",
                table: "OysterSeedTypes",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_RaftMaterialLines_RecordId",
                table: "RaftMaterialLines",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_RaftMaterialRecords_RecordedByAccountId",
                table: "RaftMaterialRecords",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "UX_RaftMaterialRecords_RaftId_RaftCreation",
                table: "RaftMaterialRecords",
                column: "RaftId",
                unique: true,
                filter: "[RecordType] = N'RaftCreation'");

            migrationBuilder.CreateIndex(
                name: "IX_SeedSuppliers_StatusId",
                table: "SeedSuppliers",
                column: "StatusId");

            migrationBuilder.AddForeignKey(
                name: "FK_CultivationCycles_OysterSeedTypes_PlannedSeedTypeId",
                table: "CultivationCycles",
                column: "PlannedSeedTypeId",
                principalTable: "OysterSeedTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeedReceiptLots_OysterSeedTypes_SeedTypeId",
                table: "SeedReceiptLots",
                column: "SeedTypeId",
                principalTable: "OysterSeedTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeedReceiptLots_SeedSuppliers_SupplierId",
                table: "SeedReceiptLots",
                column: "SupplierId",
                principalTable: "SeedSuppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
