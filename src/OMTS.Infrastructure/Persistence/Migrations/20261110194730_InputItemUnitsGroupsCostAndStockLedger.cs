using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InputItemUnitsGroupsCostAndStockLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "PurchaseReturnLines",
                type: "decimal(18,5)",
                precision: 18,
                scale: 5,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AddColumn<decimal>(
                name: "EnteredQuantity",
                table: "PurchaseReturnLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "EnteredUnitPrice",
                table: "PurchaseReturnLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "ItemUnitId",
                table: "PurchaseReturnLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitFactor",
                table: "PurchaseReturnLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "PurchaseReceiptLines",
                type: "decimal(18,5)",
                precision: 18,
                scale: 5,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,3)",
                oldPrecision: 18,
                oldScale: 3,
                oldNullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EnteredDiscount",
                table: "PurchaseReceiptLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "EnteredQuantity",
                table: "PurchaseReceiptLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EnteredUnitPrice",
                table: "PurchaseReceiptLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ItemUnitId",
                table: "PurchaseReceiptLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitFactor",
                table: "PurchaseReceiptLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<decimal>(
                name: "CostPrice",
                table: "FarmingInputItems",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "FarmingInputItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ItemGroupId",
                table: "FarmingInputItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastPurchasePrice",
                table: "FarmingInputItems",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxQuantity",
                table: "FarmingInputItems",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinQuantity",
                table: "FarmingInputItems",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "FarmingInputItems",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowNegativeInputQuantity",
                table: "Cooperatives",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "InputCostMethod",
                table: "Cooperatives",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Fixed");

            migrationBuilder.CreateTable(
                name: "InputItemGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputItemGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InputItemPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputItemPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InputItemPhotos_FarmingInputItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "FarmingInputItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InputStockMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeedLotLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,5)", precision: 18, scale: 5, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TransactionPrice = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PartyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReversesMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputStockMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InputStockMovements_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputStockMovements_FarmingInputItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "FarmingInputItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InputUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputUnits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InputItemUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Factor = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsBase = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputItemUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InputItemUnits_FarmingInputItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "FarmingInputItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputItemUnits_InputUnits_UnitId",
                        column: x => x.UnitId,
                        principalTable: "InputUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_ItemUnitId",
                table: "PurchaseReturnLines",
                column: "ItemUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceiptLines_ItemUnitId",
                table: "PurchaseReceiptLines",
                column: "ItemUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_FarmingInputItems_IsHidden",
                table: "FarmingInputItems",
                column: "IsHidden");

            migrationBuilder.CreateIndex(
                name: "IX_FarmingInputItems_ItemGroupId",
                table: "FarmingInputItems",
                column: "ItemGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_InputItemGroups_Name",
                table: "InputItemGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InputItemPhotos_ItemId",
                table: "InputItemPhotos",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InputItemUnits_Code",
                table: "InputItemUnits",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InputItemUnits_ItemId_Base",
                table: "InputItemUnits",
                column: "ItemId",
                unique: true,
                filter: "[IsBase] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_InputItemUnits_ItemId_UnitId",
                table: "InputItemUnits",
                columns: new[] { "ItemId", "UnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InputItemUnits_UnitId",
                table: "InputItemUnits",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_InputStockMovements_DocumentId",
                table: "InputStockMovements",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_InputStockMovements_ItemId_OccurredAt",
                table: "InputStockMovements",
                columns: new[] { "ItemId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InputStockMovements_RecordedByAccountId",
                table: "InputStockMovements",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_InputStockMovements_SeedLotLineId",
                table: "InputStockMovements",
                column: "SeedLotLineId");

            migrationBuilder.CreateIndex(
                name: "IX_InputStockMovements_Sequence",
                table: "InputStockMovements",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InputUnits_Name",
                table: "InputUnits",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FarmingInputItems_InputItemGroups_ItemGroupId",
                table: "FarmingInputItems",
                column: "ItemGroupId",
                principalTable: "InputItemGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReceiptLines_InputItemUnits_ItemUnitId",
                table: "PurchaseReceiptLines",
                column: "ItemUnitId",
                principalTable: "InputItemUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturnLines_InputItemUnits_ItemUnitId",
                table: "PurchaseReturnLines",
                column: "ItemUnitId",
                principalTable: "InputItemUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            BackfillUnitsLinesCostAndLedger(migrationBuilder);
        }

        /// <summary>
        /// Moves the data that already exists onto the new model, in one go with the schema change: the unit catalogue from the distinct unit texts of the items (regardless of letter
        /// case), one base unit per item (factor 1, code = item code + "-" + the first letter of the unit without diacritics, a digit appended on a clash), the existing receipt and return
        /// lines linked to it with their values copied as typed, the cost price and last purchase price of the items from their Completed receipts, and the movement ledger from the
        /// Completed receipts and Recorded returns in the order they happened. Item codes are not touched.
        /// </summary>
        private static void BackfillUnitsLinesCostAndLedger(MigrationBuilder migrationBuilder)
        {
            const string vietnamese = "àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵ";
            var plain = string.Concat(vietnamese.Select(c => c == 'đ' ? 'd' : c.ToString().Normalize(System.Text.NormalizationForm.FormD)[0]));
            var firstLetter = $"TRANSLATE(LOWER(LEFT(LTRIM(i.[Unit]), 1)), N'{vietnamese}', N'{plain}')";

            migrationBuilder.Sql(
                """
                INSERT INTO [InputUnits] ([Id], [Name], [CreatedAt])
                SELECT NEWID(), MIN(i.[Unit]), SWITCHOFFSET(SYSDATETIMEOFFSET(), '+07:00')
                FROM [FarmingInputItems] i
                WHERE LEN(LTRIM(RTRIM(i.[Unit]))) > 0
                GROUP BY i.[Unit];
                """);

            migrationBuilder.Sql(
                $"""
                ;WITH candidates AS (
                    SELECT i.[Id] AS [ItemId], u.[Id] AS [UnitId], i.[Code],
                           i.[Code] + '-' + CASE WHEN {firstLetter} LIKE '[a-z0-9]' THEN UPPER({firstLetter}) ELSE 'U' END AS [Candidate]
                    FROM [FarmingInputItems] i
                    INNER JOIN [InputUnits] u ON u.[Name] = i.[Unit]
                ), ranked AS (
                    SELECT c.*, ROW_NUMBER() OVER (PARTITION BY c.[Candidate] ORDER BY c.[Code]) AS [Rn] FROM candidates c
                )
                INSERT INTO [InputItemUnits] ([Id], [ItemId], [UnitId], [Factor], [Code], [IsBase])
                SELECT NEWID(), r.[ItemId], r.[UnitId], 1,
                       CASE WHEN r.[Rn] = 1 THEN r.[Candidate] ELSE r.[Candidate] + CAST(r.[Rn] - 1 AS nvarchar(10)) END, 1
                FROM ranked r;
                """);

            migrationBuilder.Sql(
                """
                UPDATE l SET l.[ItemUnitId] = iu.[Id], l.[UnitFactor] = 1,
                             l.[EnteredQuantity] = l.[Quantity], l.[EnteredUnitPrice] = l.[UnitPrice], l.[EnteredDiscount] = l.[Discount]
                FROM [PurchaseReceiptLines] l
                LEFT JOIN [InputItemUnits] iu ON iu.[ItemId] = l.[ItemId] AND iu.[IsBase] = 1;

                UPDATE l SET l.[ItemUnitId] = iu.[Id], l.[UnitFactor] = 1, l.[EnteredQuantity] = l.[Quantity], l.[EnteredUnitPrice] = l.[UnitPrice]
                FROM [PurchaseReturnLines] l
                LEFT JOIN [InputItemUnits] iu ON iu.[ItemId] = l.[ItemId] AND iu.[IsBase] = 1;
                """);

            // The cost of an item with a Completed receipt is the price of its first one (the Fixed rule as if it had always applied); the last purchase price is that of its latest.
            migrationBuilder.Sql(
                """
                ;WITH priced AS (
                    SELECT l.[ItemId], l.[UnitPrice] - l.[Discount] AS [Price],
                           ROW_NUMBER() OVER (PARTITION BY l.[ItemId] ORDER BY r.[ReceivedAt], l.[LineNumber]) AS [FirstRn],
                           ROW_NUMBER() OVER (PARTITION BY l.[ItemId] ORDER BY r.[ReceivedAt] DESC, l.[LineNumber] DESC) AS [LastRn]
                    FROM [PurchaseReceiptLines] l
                    INNER JOIN [PurchaseReceipts] r ON r.[Id] = l.[ReceiptId]
                    WHERE r.[StatusId] = 1702 AND l.[ItemId] IS NOT NULL AND l.[UnitPrice] IS NOT NULL
                )
                UPDATE i SET i.[CostPrice] = f.[Price], i.[LastPurchasePrice] = la.[Price]
                FROM [FarmingInputItems] i
                LEFT JOIN priced f ON f.[ItemId] = i.[Id] AND f.[FirstRn] = 1
                LEFT JOIN priced la ON la.[ItemId] = i.[Id] AND la.[LastRn] = 1
                WHERE f.[ItemId] IS NOT NULL;
                """);

            // Identity values follow the ORDER BY of the insert, so the write order of the ledger is the order the documents happened.
            migrationBuilder.Sql(
                """
                INSERT INTO [InputStockMovements]
                    ([Id], [ItemId], [SeedLotLineId], [Kind], [DocumentId], [DocumentCode], [OccurredAt], [Quantity], [UnitCost], [TransactionPrice], [PartyName],
                     [ReversesMovementId], [RecordedAt], [RecordedByAccountId])
                SELECT NEWID(), m.[ItemId], m.[SeedLotLineId], m.[Kind], m.[DocumentId], m.[DocumentCode], m.[OccurredAt], m.[Quantity], m.[UnitCost], m.[TransactionPrice], m.[PartyName],
                       NULL, m.[RecordedAt], m.[RecordedByAccountId]
                FROM (
                    SELECT l.[ItemId], CASE WHEN i.[Group] = 'Seed' THEN l.[Id] END AS [SeedLotLineId], 'Receipt' AS [Kind], r.[Id] AS [DocumentId], r.[Code] AS [DocumentCode],
                           r.[ReceivedAt] AS [OccurredAt], l.[Quantity] AS [Quantity], i.[CostPrice] AS [UnitCost], l.[UnitPrice] - l.[Discount] AS [TransactionPrice],
                           s.[Name] AS [PartyName], COALESCE(r.[CompletedAt], r.[ReceivedAt]) AS [RecordedAt], r.[RecordedByAccountId] AS [RecordedByAccountId], l.[LineNumber] AS [Line]
                    FROM [PurchaseReceiptLines] l
                    INNER JOIN [PurchaseReceipts] r ON r.[Id] = l.[ReceiptId]
                    INNER JOIN [FarmingInputItems] i ON i.[Id] = l.[ItemId]
                    INNER JOIN [Suppliers] s ON s.[Id] = r.[SupplierId]
                    WHERE r.[StatusId] = 1702 AND l.[Quantity] > 0
                    UNION ALL
                    SELECT l.[ItemId], CASE WHEN i.[Group] = 'Seed' THEN l.[ReceiptLineId] END, 'PurchaseReturn', rt.[Id], rt.[Code],
                           rt.[ReturnedAt], -l.[Quantity], i.[CostPrice], l.[UnitPrice], s.[Name], rt.[ReturnedAt], rt.[RecordedByAccountId], 0
                    FROM [PurchaseReturnLines] l
                    INNER JOIN [PurchaseReturns] rt ON rt.[Id] = l.[ReturnId]
                    INNER JOIN [FarmingInputItems] i ON i.[Id] = l.[ItemId]
                    INNER JOIN [Suppliers] s ON s.[Id] = rt.[SupplierId]
                    WHERE rt.[StatusId] = 1901 AND l.[Quantity] > 0
                ) m
                ORDER BY m.[OccurredAt], m.[Line];
                """);
        }
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FarmingInputItems_InputItemGroups_ItemGroupId",
                table: "FarmingInputItems");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReceiptLines_InputItemUnits_ItemUnitId",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturnLines_InputItemUnits_ItemUnitId",
                table: "PurchaseReturnLines");

            migrationBuilder.DropTable(
                name: "InputItemGroups");

            migrationBuilder.DropTable(
                name: "InputItemPhotos");

            migrationBuilder.DropTable(
                name: "InputItemUnits");

            migrationBuilder.DropTable(
                name: "InputStockMovements");

            migrationBuilder.DropTable(
                name: "InputUnits");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturnLines_ItemUnitId",
                table: "PurchaseReturnLines");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceiptLines_ItemUnitId",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropIndex(
                name: "IX_FarmingInputItems_IsHidden",
                table: "FarmingInputItems");

            migrationBuilder.DropIndex(
                name: "IX_FarmingInputItems_ItemGroupId",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "EnteredQuantity",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "EnteredUnitPrice",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "ItemUnitId",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "UnitFactor",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "EnteredDiscount",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropColumn(
                name: "EnteredQuantity",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropColumn(
                name: "EnteredUnitPrice",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropColumn(
                name: "ItemUnitId",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropColumn(
                name: "UnitFactor",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropColumn(
                name: "CostPrice",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "ItemGroupId",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "LastPurchasePrice",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "MaxQuantity",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "MinQuantity",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "AllowNegativeInputQuantity",
                table: "Cooperatives");

            migrationBuilder.DropColumn(
                name: "InputCostMethod",
                table: "Cooperatives");

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "PurchaseReturnLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,5)",
                oldPrecision: 18,
                oldScale: 5);

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "PurchaseReceiptLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,5)",
                oldPrecision: 18,
                oldScale: 5,
                oldNullable: true);
        }
    }
}
