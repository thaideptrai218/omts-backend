using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductsSalesOrdersAndHarvestPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "SalesInvoices",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalesOrderId",
                table: "SalesInvoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Factor",
                table: "SalesInvoiceLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Quantity",
                table: "SalesInvoiceLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "SalesOrderLineId",
                table: "SalesInvoiceLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitCode",
                table: "SalesInvoiceLines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "UnitId",
                table: "SalesInvoiceLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "UnitName",
                table: "SalesInvoiceLines",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "Products",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxKg",
                table: "Products",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinKg",
                table: "Products",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Products",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HarvestPlanLineId",
                table: "HarvestLots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalesOrderId",
                table: "CustomerPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HarvestLotProductLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CageCount = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    Kg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestLotProductLines", x => x.Id);
                    table.CheckConstraint("CK_HarvestLotProductLine_CageCount", "[CageCount] > 0");
                    table.CheckConstraint("CK_HarvestLotProductLine_Kg", "[Kg] > 0");
                    table.ForeignKey(
                        name: "FK_HarvestLotProductLines_HarvestLots_LotId",
                        column: x => x.LotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestLotProductLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HarvestPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PlannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AssignedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Accounts_AssignedByAccountId",
                        column: x => x.AssignedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Accounts_CancelledByAccountId",
                        column: x => x.CancelledByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Accounts_CompletedByAccountId",
                        column: x => x.CompletedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Accounts_CreatedByAccountId",
                        column: x => x.CreatedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlans_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCategories_ProductCategories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "ProductCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductDisposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisposedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductDisposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductDisposals_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductDisposals_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductPhotos_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductPriceChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitName = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OldPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: true),
                    NewPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductPriceChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductPriceChanges_Accounts_ChangedByAccountId",
                        column: x => x.ChangedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductPriceChanges_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductStockMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HarvestLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LotCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentCode = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    QuantityKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TransactionPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: true),
                    PartyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PartyCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ReversesMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductStockMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductStockMovements_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductStockMovements_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsBase = table.Column<bool>(type: "bit", nullable: false),
                    SalePrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductUnits", x => x.Id);
                    table.CheckConstraint("CK_ProductUnit_SalePrice_Positive", "[SalePrice] > 0");
                    table.ForeignKey(
                        name: "FK_ProductUnits_InputUnits_UnitId",
                        column: x => x.UnitId,
                        principalTable: "InputUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductUnits_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OrderedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PickupAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PickupDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PurchaseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReceivedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    DiscountDecidedByOwner = table.Column<bool>(type: "bit", nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    StatusId = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedManually = table.Column<bool>(type: "bit", nullable: false),
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
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                    table.CheckConstraint("CK_SalesOrder_Amounts", "[DiscountAmount] >= 0 AND [Subtotal] >= 0 AND [Total] >= 0 AND [DiscountAmount] <= [Subtotal] AND [Total] = [Subtotal] - [DiscountAmount]");
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_CancelledByAccountId",
                        column: x => x.CancelledByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_CompletedByAccountId",
                        column: x => x.CompletedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_CreatedByAccountId",
                        column: x => x.CreatedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Accounts_ReceivedByAccountId",
                        column: x => x.ReceivedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_FarmingZones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "FarmingZones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_PurchaseRequests_PurchaseRequestId",
                        column: x => x.PurchaseRequestId,
                        principalTable: "PurchaseRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Statuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "Statuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HarvestPlanLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    RaftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpectedCageCount = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestPlanLines", x => x.Id);
                    table.CheckConstraint("CK_HarvestPlanLine_ExpectedCageCount", "[ExpectedCageCount] IS NULL OR [ExpectedCageCount] > 0");
                    table.ForeignKey(
                        name: "FK_HarvestPlanLines_HarvestPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "HarvestPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanLines_Products_ExpectedProductId",
                        column: x => x.ExpectedProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanLines_Rafts_RaftId",
                        column: x => x.RaftId,
                        principalTable: "Rafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductDisposalLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HarvestLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductDisposalLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductDisposalLines_HarvestLots_HarvestLotId",
                        column: x => x.HarvestLotId,
                        principalTable: "HarvestLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductDisposalLines_ProductDisposals_DisposalId",
                        column: x => x.DisposalId,
                        principalTable: "ProductDisposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductDisposalLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderHarvestPlans",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderHarvestPlans", x => new { x.OrderId, x.PlanId });
                    table.ForeignKey(
                        name: "FK_SalesOrderHarvestPlans_HarvestPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "HarvestPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderHarvestPlans_SalesOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ListPrice = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderLines", x => x.Id);
                    table.CheckConstraint("CK_SalesOrderLine_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [Amount] >= 0");
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_SalesOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HarvestPlanLanes",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LaneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SkippedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SkippedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SkipReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestPlanLanes", x => new { x.LineId, x.LaneId });
                    table.CheckConstraint("CK_HarvestPlanLane_Skipped", "([SkippedAt] IS NULL AND [SkipReason] IS NULL AND [SkippedByAccountId] IS NULL) OR ([SkippedAt] IS NOT NULL AND [SkipReason] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_HarvestPlanLanes_Accounts_SkippedByAccountId",
                        column: x => x.SkippedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanLanes_HarvestPlanLines_LineId",
                        column: x => x.LineId,
                        principalTable: "HarvestPlanLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestPlanLanes_Lanes_LaneId",
                        column: x => x.LaneId,
                        principalTable: "Lanes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 3206, "ORDERED", null, "Ordered", "PURCHASE_REQUEST", true, false, "The Cooperative Owner made an order (DH) from the request; the order carries on from here.", 6 },
                    { 4201, "DRAFT", null, "Draft", "HARVEST_PLAN", false, true, "Being prepared by the Owner; the Zone Managers do not see it yet.", 1 },
                    { 4202, "ASSIGNED", null, "Assigned", "HARVEST_PLAN", false, false, "Handed to the Zone Managers of its zone; no lot is recorded and no lane left out yet.", 2 },
                    { 4203, "HARVESTING", null, "Harvesting", "HARVEST_PLAN", false, false, "A lot is recorded from a line of the plan, or a lane is left out.", 3 },
                    { 4204, "COMPLETED", null, "Completed", "HARVEST_PLAN", false, false, "Every line has its lot or has every lane left out, or the Owner finished the plan by hand; removing a lot of a plan finished by itself opens it again.", 4 },
                    { 4205, "CANCELLED", null, "Cancelled", "HARVEST_PLAN", true, false, "Cancelled with a reason before any lot was recorded.", 5 },
                    { 4301, "DRAFT", null, "Draft", "PRODUCT_DISPOSAL", false, true, "Being prepared; holds no kg. Removed, never cancelled.", 1 },
                    { 4302, "COMPLETED", null, "Completed", "PRODUCT_DISPOSAL", false, false, "The kg have left the stock of their products; may still be edited, which writes them again.", 2 },
                    { 4303, "CANCELLED", null, "Cancelled", "PRODUCT_DISPOSAL", true, false, "Recorded by mistake; the kg are back in the stock and it counts nowhere.", 3 },
                    { 4401, "DRAFT", null, "Draft", "SALES_ORDER", false, true, "The order is agreed with the buyer; no invoice has been made from it yet. Its advance is reserved for it.", 1 },
                    { 4402, "DELIVERING", null, "Delivering", "SALES_ORDER", false, false, "At least one invoice was made from the order; goods are still to be delivered. Its advance is reserved for it.", 2 },
                    { 4403, "COMPLETED", null, "Completed", "SALES_ORDER", false, false, "Finished by hand or fully delivered; what is left of its advance is general credit of the customer. A cancelled invoice reopens it.", 3 },
                    { 4404, "CANCELLED", null, "Cancelled", "SALES_ORDER", true, false, "Cancelled with a reason; what is left of its advance is general credit of the customer.", 4 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30100, "Cooperative Owner; Zone Manager", 4301, "The disposal has a reason and its lines are complete.", false, 4302, "Action", "PRODUCT-DISPOSAL" },
                    { 30101, "Cooperative Owner; Zone Manager", 4302, "A disposal recorded by mistake; its kg go back in the stock.", true, 4303, "Action", "PRODUCT-DISPOSAL" },
                    { 30102, "System", 4401, "The first invoice of the order is saved with goods still to deliver.", false, 4402, "Action", "SALES-ORDER" },
                    { 30103, "System; Cooperative Owner", 4401, "An invoice covers every line of the order, or the Owner finishes the order.", false, 4403, "Action", "SALES-ORDER" },
                    { 30104, "Cooperative Owner; Zone Manager; System", 4402, "The Owner or a Zone Manager of the zone finishes the order, or every line is delivered.", false, 4403, "Action", "SALES-ORDER" },
                    { 30105, "Cooperative Owner", 4401, "A reason is given.", true, 4404, "Action", "SALES-ORDER" },
                    { 30106, "Cooperative Owner", 4402, "A reason is given and no invoice of the order is left uncancelled; otherwise the order is finished instead.", true, 4404, "Action", "SALES-ORDER" },
                    { 30107, "System", 4403, "An invoice of the finished order is cancelled: the order is open again.", false, 4402, "Action", "SALES-ORDER" },
                    { 30108, "Cooperative Owner", 3201, "The Owner saves an order made from the request.", false, 3206, "Action", "SALES-ORDER" },
                    { 30109, "Cooperative Owner", 3202, "The request is not Fulfilled and has no order yet; the Owner saves an order made from it.", false, 3206, "Action", "SALES-ORDER" },
                    { 30110, "Cooperative Owner", 4201, "The plan has lines, every raft has an Open cycle and no lane is in another open plan; the Zone Managers of the zone are told.", false, 4202, "Action", "HARVEST-PLAN" },
                    { 30111, "System", 4202, "The first lot is recorded from a line of the plan, or the first lane is left out.", false, 4203, "Action", "HARVEST-PLAN" },
                    { 30112, "Cooperative Owner; System", 4203, "Every line has its lot or every lane left out, or the Owner finishes the plan: lanes still to do stay as they are.", false, 4204, "Action", "HARVEST-PLAN" },
                    { 30113, "System", 4202, "Every lane of every line is left out and no lot was recorded.", false, 4204, "Action", "HARVEST-PLAN" },
                    { 30114, "Cooperative Owner", 4201, "A reason is given.", true, 4205, "Action", "HARVEST-PLAN" },
                    { 30115, "Cooperative Owner", 4202, "A reason is given and no lot was recorded from the plan.", true, 4205, "Action", "HARVEST-PLAN" },
                    { 30116, "System", 4204, "A lot of a plan that finished by itself is removed, or a lane left out is put back: a line is open again.", false, 4203, "Action", "HARVEST-PLAN" }
                });

            // Products made before the catalogue carry no code (empty): they take the next SP codes in the order they were created, and the SP counter continues after the highest one.
            migrationBuilder.Sql(
                """
                DECLARE @start BIGINT = COALESCE((SELECT MAX(TRY_CAST(SUBSTRING(Code, 3, 20) AS BIGINT)) FROM Products WHERE Code LIKE N'SP[0-9]%'), 0);
                UPDATE p SET p.Code = N'SP' + FORMAT(@start + n.Number, '000000')
                FROM Products p
                JOIN (SELECT Id, ROW_NUMBER() OVER (ORDER BY CreatedAt, Id) AS Number FROM Products WHERE Code = N'') n ON n.Id = p.Id;
                DECLARE @last BIGINT = COALESCE((SELECT MAX(TRY_CAST(SUBSTRING(Code, 3, 20) AS BIGINT)) FROM Products WHERE Code LIKE N'SP[0-9]%'), 0);
                IF EXISTS (SELECT 1 FROM DocumentSequences WHERE [Key] = N'SP')
                    UPDATE DocumentSequences SET LastValue = CASE WHEN LastValue < @last THEN @last ELSE LastValue END WHERE [Key] = N'SP';
                ELSE
                    INSERT INTO DocumentSequences ([Key], LastValue) VALUES (N'SP', @last);
                """);

            // Every product with a kg price counts in kg: the unit "kg" joins the shared unit list when it is not there, and the product gets its base unit at its list price. A product with no kg price yet
            // (a cage-only catalogue entry) gets none until the Owner prices it.
            migrationBuilder.Sql(
                """
                IF NOT EXISTS (SELECT 1 FROM InputUnits WHERE Name = N'kg')
                    INSERT INTO InputUnits (Id, Name, CreatedAt) VALUES (NEWID(), N'kg', SYSDATETIMEOFFSET());
                INSERT INTO ProductUnits (Id, ProductId, UnitId, Factor, Code, IsBase, SalePrice)
                SELECT NEWID(), p.Id, (SELECT TOP 1 Id FROM InputUnits WHERE Name = N'kg'), 1, p.Code + N'-K', 1, p.UnitPrice
                FROM Products p
                WHERE p.UnitPrice > 0 AND p.Code <> N'';
                """);

            // The price history starts with the price each product has now, per kg (the base unit).
            migrationBuilder.Sql(
                """
                INSERT INTO ProductPriceChanges (Id, ProductId, UnitId, UnitCode, UnitName, OldPrice, NewPrice, ChangedAt, ChangedByAccountId)
                SELECT NEWID(), p.Id, b.UnitId, b.Code, i.Name, NULL, p.UnitPrice, p.CreatedAt, NULL
                FROM Products p
                JOIN ProductUnits b ON b.ProductId = p.Id AND b.IsBase = 1
                JOIN InputUnits i ON i.Id = b.UnitId;
                """);

            // The numbers of the orders (DH000001) and of the harvest plans (KHT000001) start at 1; the allocator would create the row on first use, this only makes it visible from the start.
            migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM DocumentSequences WHERE [Key] = N'DH') INSERT INTO DocumentSequences ([Key], LastValue) VALUES (N'DH', 0);");
            migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM DocumentSequences WHERE [Key] = N'KHT') INSERT INTO DocumentSequences ([Key], LastValue) VALUES (N'KHT', 0);");

            // Every existing invoice line was weighed and priced in kg: it takes the base unit of its product (quantity = kg, factor 1), so its unit price keeps its meaning; a product with no base unit
            // falls back to the shared kg unit.
            migrationBuilder.Sql(
                """
                UPDATE l SET l.UnitId = pu.UnitId, l.UnitCode = pu.Code, l.UnitName = iu.Name, l.Factor = 1, l.Quantity = l.Kg
                FROM SalesInvoiceLines l
                JOIN ProductUnits pu ON pu.ProductId = l.ProductId AND pu.IsBase = 1
                JOIN InputUnits iu ON iu.Id = pu.UnitId;

                IF NOT EXISTS (SELECT 1 FROM InputUnits WHERE Name = N'kg')
                    INSERT INTO InputUnits (Id, Name, CreatedAt) VALUES (NEWID(), N'kg', SYSDATETIMEOFFSET());
                UPDATE l SET l.UnitId = (SELECT TOP 1 Id FROM InputUnits WHERE Name = N'kg'), l.UnitCode = N'kg', l.UnitName = N'kg', l.Factor = 1, l.Quantity = l.Kg
                FROM SalesInvoiceLines l
                WHERE l.Quantity = 0;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoices_SalesOrderId",
                table: "SalesInvoices",
                column: "SalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoiceLines_SalesOrderLineId",
                table: "SalesInvoiceLines",
                column: "SalesOrderLineId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SalesInvoiceLine_Quantity",
                table: "SalesInvoiceLines",
                sql: "[Quantity] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Products_CategoryId",
                table: "Products",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_IsHidden",
                table: "Products",
                column: "IsHidden");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_HarvestPlanLineId",
                table: "HarvestLots",
                column: "HarvestPlanLineId",
                unique: true,
                filter: "[HarvestPlanLineId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_SalesOrderId",
                table: "CustomerPayments",
                column: "SalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotProductLines_LotId_LineNumber",
                table: "HarvestLotProductLines",
                columns: new[] { "LotId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLotProductLines_ProductId",
                table: "HarvestLotProductLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLanes_LaneId",
                table: "HarvestPlanLanes",
                column: "LaneId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLanes_SkippedByAccountId",
                table: "HarvestPlanLanes",
                column: "SkippedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLines_ExpectedProductId",
                table: "HarvestPlanLines",
                column: "ExpectedProductId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLines_PlanId_LineNumber",
                table: "HarvestPlanLines",
                columns: new[] { "PlanId", "LineNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlanLines_RaftId",
                table: "HarvestPlanLines",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_AssignedByAccountId",
                table: "HarvestPlans",
                column: "AssignedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_CancelledByAccountId",
                table: "HarvestPlans",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_Code",
                table: "HarvestPlans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_CompletedByAccountId",
                table: "HarvestPlans",
                column: "CompletedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_CreatedByAccountId",
                table: "HarvestPlans",
                column: "CreatedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestPlans_StatusId_PlannedDate",
                table: "HarvestPlans",
                columns: new[] { "StatusId", "PlannedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_Name",
                table: "ProductCategories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductCategories_ParentId",
                table: "ProductCategories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductDisposalLines_DisposalId_LineNumber",
                table: "ProductDisposalLines",
                columns: new[] { "DisposalId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductDisposalLines_HarvestLotId",
                table: "ProductDisposalLines",
                column: "HarvestLotId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductDisposalLines_ProductId",
                table: "ProductDisposalLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductDisposals_Code",
                table: "ProductDisposals",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductDisposals_DisposedAt",
                table: "ProductDisposals",
                column: "DisposedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProductDisposals_StatusId",
                table: "ProductDisposals",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductDisposals_ZoneId",
                table: "ProductDisposals",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductPhotos_ProductId",
                table: "ProductPhotos",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceChanges_ChangedByAccountId",
                table: "ProductPriceChanges",
                column: "ChangedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceChanges_ProductId_ChangedAt",
                table: "ProductPriceChanges",
                columns: new[] { "ProductId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductPriceChanges_ProductId_UnitId",
                table: "ProductPriceChanges",
                columns: new[] { "ProductId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductStockMovements_DocumentId",
                table: "ProductStockMovements",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductStockMovements_HarvestLotId",
                table: "ProductStockMovements",
                column: "HarvestLotId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductStockMovements_ProductId_OccurredAt",
                table: "ProductStockMovements",
                columns: new[] { "ProductId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductStockMovements_RecordedByAccountId",
                table: "ProductStockMovements",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductStockMovements_ReversesMovementId",
                table: "ProductStockMovements",
                column: "ReversesMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductStockMovements_Sequence",
                table: "ProductStockMovements",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_Code",
                table: "ProductUnits",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId_Base",
                table: "ProductUnits",
                column: "ProductId",
                unique: true,
                filter: "[IsBase] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId_UnitId",
                table: "ProductUnits",
                columns: new[] { "ProductId", "UnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_UnitId",
                table: "ProductUnits",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderHarvestPlans_PlanId",
                table: "SalesOrderHarvestPlans",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_OrderId_LineNumber",
                table: "SalesOrderLines",
                columns: new[] { "OrderId", "LineNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_ProductId",
                table: "SalesOrderLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CancelledByAccountId",
                table: "SalesOrders",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_Code",
                table: "SalesOrders",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CompletedByAccountId",
                table: "SalesOrders",
                column: "CompletedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CreatedByAccountId",
                table: "SalesOrders",
                column: "CreatedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CustomerId",
                table: "SalesOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_DeduplicationKey",
                table: "SalesOrders",
                column: "DeduplicationKey",
                unique: true,
                filter: "[DeduplicationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_PickupDate",
                table: "SalesOrders",
                column: "PickupDate");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_PurchaseRequestId",
                table: "SalesOrders",
                column: "PurchaseRequestId",
                unique: true,
                filter: "[PurchaseRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_ReceivedByAccountId",
                table: "SalesOrders",
                column: "ReceivedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_StatusId_OrderDate",
                table: "SalesOrders",
                columns: new[] { "StatusId", "OrderDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_ZoneId",
                table: "SalesOrders",
                column: "ZoneId");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_SalesOrders_SalesOrderId",
                table: "CustomerPayments",
                column: "SalesOrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HarvestLots_HarvestPlanLines_HarvestPlanLineId",
                table: "HarvestLots",
                column: "HarvestPlanLineId",
                principalTable: "HarvestPlanLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ProductCategories_CategoryId",
                table: "Products",
                column: "CategoryId",
                principalTable: "ProductCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoiceLines_SalesOrderLines_SalesOrderLineId",
                table: "SalesInvoiceLines",
                column: "SalesOrderLineId",
                principalTable: "SalesOrderLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesInvoices_SalesOrders_SalesOrderId",
                table: "SalesInvoices",
                column: "SalesOrderId",
                principalTable: "SalesOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The orders go with the tables below: a request made into an order waits for its answer again.
            migrationBuilder.Sql("UPDATE PurchaseRequests SET StatusId = 3201 WHERE StatusId = 3206;");
            migrationBuilder.Sql("DELETE FROM DocumentSequences WHERE [Key] IN (N'DH', N'KHT', N'XHS');");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_SalesOrders_SalesOrderId",
                table: "CustomerPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_HarvestLots_HarvestPlanLines_HarvestPlanLineId",
                table: "HarvestLots");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_ProductCategories_CategoryId",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoiceLines_SalesOrderLines_SalesOrderLineId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoices_SalesOrders_SalesOrderId",
                table: "SalesInvoices");

            migrationBuilder.DropTable(
                name: "HarvestLotProductLines");

            migrationBuilder.DropTable(
                name: "HarvestPlanLanes");

            migrationBuilder.DropTable(
                name: "ProductCategories");

            migrationBuilder.DropTable(
                name: "ProductDisposalLines");

            migrationBuilder.DropTable(
                name: "ProductPhotos");

            migrationBuilder.DropTable(
                name: "ProductPriceChanges");

            migrationBuilder.DropTable(
                name: "ProductStockMovements");

            migrationBuilder.DropTable(
                name: "ProductUnits");

            migrationBuilder.DropTable(
                name: "SalesOrderHarvestPlans");

            migrationBuilder.DropTable(
                name: "SalesOrderLines");

            migrationBuilder.DropTable(
                name: "HarvestPlanLines");

            migrationBuilder.DropTable(
                name: "ProductDisposals");

            migrationBuilder.DropTable(
                name: "SalesOrders");

            migrationBuilder.DropTable(
                name: "HarvestPlans");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_SalesOrderId",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoiceLines_SalesOrderLineId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SalesInvoiceLine_Quantity",
                table: "SalesInvoiceLines");

            migrationBuilder.DropIndex(
                name: "IX_Products_CategoryId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_IsHidden",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLots_HarvestPlanLineId",
                table: "HarvestLots");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_SalesOrderId",
                table: "CustomerPayments");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30100);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30101);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30102);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30103);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30104);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30105);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30106);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30107);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30108);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30109);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30110);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30111);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30112);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30113);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30114);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30115);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30116);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3206);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4201);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4202);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4203);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4204);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4205);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4301);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4302);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4303);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4401);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4402);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4403);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4404);

            migrationBuilder.DropColumn(
                name: "Note",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "SalesOrderId",
                table: "SalesInvoices");

            migrationBuilder.DropColumn(
                name: "Factor",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "SalesOrderLineId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "UnitCode",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "UnitName",
                table: "SalesInvoiceLines");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MaxKg",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MinKg",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "HarvestPlanLineId",
                table: "HarvestLots");

            migrationBuilder.DropColumn(
                name: "SalesOrderId",
                table: "CustomerPayments");
        }
    }
}
