using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductCatalogueAndLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "Products",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Products",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Products",
                type: "nvarchar(2000)",
                maxLength: 2000,
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

            migrationBuilder.CreateTable(
                name: "HarvestLotProductLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CageCount = table.Column<int>(type: "int", nullable: false),
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
                    IsBase = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductUnits", x => x.Id);
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

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 4001, "DRAFT", null, "Draft", "PRODUCT_DISPOSAL", false, true, "Being prepared; holds no kg. Removed, never cancelled.", 1 },
                    { 4002, "COMPLETED", null, "Completed", "PRODUCT_DISPOSAL", false, false, "The kg have left the stock of their products; may still be edited, which writes them again.", 2 },
                    { 4003, "CANCELLED", null, "Cancelled", "PRODUCT_DISPOSAL", true, false, "Recorded by mistake; the kg are back in the stock and it counts nowhere.", 3 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30082, "Cooperative Owner; Zone Manager", 4001, "The disposal has a reason and its lines are complete.", false, 4002, "Action", "PRODUCT-DISPOSAL" },
                    { 30083, "Cooperative Owner; Zone Manager", 4002, "A disposal recorded by mistake; its kg go back in the stock.", true, 4003, "Action", "PRODUCT-DISPOSAL" }
                });

            // Existing products take codes in the order they were created, and the SP counter continues after them.
            migrationBuilder.Sql(
                """
                UPDATE p SET p.Code = n.Code
                FROM Products p
                JOIN (SELECT Id, N'SP' + FORMAT(ROW_NUMBER() OVER (ORDER BY CreatedAt, Id), '000000') AS Code
                      FROM Products) n ON n.Id = p.Id;
                DELETE FROM DocumentSequences WHERE [Key] = N'SP';
                INSERT INTO DocumentSequences ([Key], LastValue)
                SELECT N'SP', COUNT_BIG(*) FROM Products;
                """);

            // Every existing product counts in kg: the unit "kg" joins the shared unit list when it is not there, and each product gets its base unit.
            migrationBuilder.Sql(
                """
                IF NOT EXISTS (SELECT 1 FROM InputUnits WHERE Name = N'kg')
                    INSERT INTO InputUnits (Id, Name, CreatedAt) VALUES (NEWID(), N'kg', SYSDATETIMEOFFSET());
                INSERT INTO ProductUnits (Id, ProductId, UnitId, Factor, Code, IsBase)
                SELECT NEWID(), p.Id, (SELECT TOP 1 Id FROM InputUnits WHERE Name = N'kg'), 1, p.Code + N'-K', 1
                FROM Products p;
                """);

            // The price history starts with the price each existing product has now.
            migrationBuilder.Sql(
                """
                INSERT INTO ProductPriceChanges (Id, ProductId, OldPrice, NewPrice, ChangedAt, ChangedByAccountId)
                SELECT NEWID(), p.Id, NULL, p.UnitPrice, p.CreatedAt, NULL
                FROM Products p;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Products_CategoryId",
                table: "Products",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Code",
                table: "Products",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_IsHidden",
                table: "Products",
                column: "IsHidden");

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

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ProductCategories_CategoryId",
                table: "Products",
                column: "CategoryId",
                principalTable: "ProductCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM DocumentSequences WHERE [Key] IN (N'SP', N'XHS');");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_ProductCategories_CategoryId",
                table: "Products");

            migrationBuilder.DropTable(
                name: "HarvestLotProductLines");

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
                name: "ProductDisposals");

            migrationBuilder.DropIndex(
                name: "IX_Products_CategoryId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_Code",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_IsHidden",
                table: "Products");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30082);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30083);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4001);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4002);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 4003);

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Description",
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

            // The kg unit the Up step may have added to the shared list goes again unless an input item counts in it; a unit made before this migration stays.
            migrationBuilder.Sql(
                """
                DELETE FROM InputUnits
                WHERE Name = N'kg' AND CreatedAt >= '2026-11-11T00:00:00+07:00'
                  AND NOT EXISTS (SELECT 1 FROM InputItemUnits WHERE InputItemUnits.UnitId = InputUnits.Id);
                """);
        }
    }
}
