using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VatRateCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VatRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Percent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    IsExempt = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatRates", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "VatRates",
                columns: new[] { "Id", "CreatedAt", "CreatedBy", "IsActive", "IsDefault", "IsExempt", "IsSystem", "Name", "Percent", "SortOrder", "UpdatedAt", "UpdatedBy" },
                values: new object[,]
                {
                    { new Guid("7a1c4e20-0000-4b7a-8c10-000000000001"), new DateTimeOffset(new DateTime(2026, 11, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, true, false, true, true, "KCT", null, 0, null, null },
                    { new Guid("7a1c4e20-0000-4b7a-8c10-000000000002"), new DateTimeOffset(new DateTime(2026, 11, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, true, false, false, false, "0%", 0m, 1, null, null },
                    { new Guid("7a1c4e20-0000-4b7a-8c10-000000000003"), new DateTimeOffset(new DateTime(2026, 11, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, true, false, false, false, "5%", 5m, 2, null, null },
                    { new Guid("7a1c4e20-0000-4b7a-8c10-000000000004"), new DateTimeOffset(new DateTime(2026, 11, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, true, false, false, false, "8%", 8m, 3, null, null },
                    { new Guid("7a1c4e20-0000-4b7a-8c10-000000000005"), new DateTimeOffset(new DateTime(2026, 11, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, true, true, false, false, "10%", 10m, 4, null, null }
                });

            // Items: the enum name ("Kct", "Zero", "Five", "Eight", "Ten") becomes the seeded rate. Every column is added first, the data is carried over, then the old columns go.
            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseVatRateId",
                table: "FarmingInputItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("7a1c4e20-0000-4b7a-8c10-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "VatRateId",
                table: "PurchaseReceiptLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatRateName",
                table: "PurchaseReceiptLines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VatPercent",
                table: "PurchaseReceiptLines",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VatRateId",
                table: "PurchaseReturnLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatRateName",
                table: "PurchaseReturnLines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VatPercent",
                table: "PurchaseReturnLines",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.Sql(
                @"UPDATE FarmingInputItems SET PurchaseVatRateId = CASE PurchaseVatRate
                    WHEN 'Zero' THEN '7a1c4e20-0000-4b7a-8c10-000000000002'
                    WHEN 'Five' THEN '7a1c4e20-0000-4b7a-8c10-000000000003'
                    WHEN 'Eight' THEN '7a1c4e20-0000-4b7a-8c10-000000000004'
                    WHEN 'Ten' THEN '7a1c4e20-0000-4b7a-8c10-000000000005'
                    ELSE '7a1c4e20-0000-4b7a-8c10-000000000001' END");

            // Lines keep the rate they had as a snapshot (id, name, percent). A line with no rate stays without one, and VatAmount is not touched, so every receipt, return and supplier debt keeps its figures.
            foreach (var table in new[] { "PurchaseReceiptLines", "PurchaseReturnLines" })
            {
                migrationBuilder.Sql(
                    $@"UPDATE {table} SET
                        VatRateId = CASE VatRate WHEN 'Zero' THEN '7a1c4e20-0000-4b7a-8c10-000000000002' WHEN 'Five' THEN '7a1c4e20-0000-4b7a-8c10-000000000003' WHEN 'Eight' THEN '7a1c4e20-0000-4b7a-8c10-000000000004' WHEN 'Ten' THEN '7a1c4e20-0000-4b7a-8c10-000000000005' ELSE '7a1c4e20-0000-4b7a-8c10-000000000001' END,
                        VatRateName = CASE VatRate WHEN 'Zero' THEN N'0%' WHEN 'Five' THEN N'5%' WHEN 'Eight' THEN N'8%' WHEN 'Ten' THEN N'10%' ELSE N'KCT' END,
                        VatPercent = CASE VatRate WHEN 'Zero' THEN 0 WHEN 'Five' THEN 5 WHEN 'Eight' THEN 8 WHEN 'Ten' THEN 10 ELSE NULL END
                      WHERE VatRate IS NOT NULL");
            }

            migrationBuilder.DropColumn(
                name: "PurchaseVatRate",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "VatRate",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropColumn(
                name: "VatRate",
                table: "PurchaseReturnLines");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_VatRateId",
                table: "PurchaseReturnLines",
                column: "VatRateId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceiptLines_VatRateId",
                table: "PurchaseReceiptLines",
                column: "VatRateId");

            migrationBuilder.CreateIndex(
                name: "IX_FarmingInputItems_PurchaseVatRateId",
                table: "FarmingInputItems",
                column: "PurchaseVatRateId");

            migrationBuilder.CreateIndex(
                name: "IX_VatRates_Name",
                table: "VatRates",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FarmingInputItems_VatRates_PurchaseVatRateId",
                table: "FarmingInputItems",
                column: "PurchaseVatRateId",
                principalTable: "VatRates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReceiptLines_VatRates_VatRateId",
                table: "PurchaseReceiptLines",
                column: "VatRateId",
                principalTable: "VatRates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReturnLines_VatRates_VatRateId",
                table: "PurchaseReturnLines",
                column: "VatRateId",
                principalTable: "VatRates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PurchaseVatRate",
                table: "FarmingInputItems",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Kct");

            migrationBuilder.AddColumn<string>(
                name: "VatRate",
                table: "PurchaseReceiptLines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatRate",
                table: "PurchaseReturnLines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // The enum knew five rates: a percent it does not know goes to the nearest one below it (rates the Owner added have no place in the old shape).
            migrationBuilder.Sql(
                @"UPDATE i SET PurchaseVatRate = CASE
                    WHEN r.[Percent] IS NULL THEN 'Kct' WHEN r.[Percent] >= 10 THEN 'Ten' WHEN r.[Percent] >= 8 THEN 'Eight' WHEN r.[Percent] >= 5 THEN 'Five' ELSE 'Zero' END
                  FROM FarmingInputItems i JOIN VatRates r ON r.Id = i.PurchaseVatRateId");
            foreach (var table in new[] { "PurchaseReceiptLines", "PurchaseReturnLines" })
            {
                migrationBuilder.Sql(
                    $@"UPDATE {table} SET VatRate = CASE
                        WHEN VatPercent IS NULL THEN 'Kct' WHEN VatPercent >= 10 THEN 'Ten' WHEN VatPercent >= 8 THEN 'Eight' WHEN VatPercent >= 5 THEN 'Five' ELSE 'Zero' END
                      WHERE VatRateId IS NOT NULL");
            }

            migrationBuilder.DropForeignKey(
                name: "FK_FarmingInputItems_VatRates_PurchaseVatRateId",
                table: "FarmingInputItems");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReceiptLines_VatRates_VatRateId",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReturnLines_VatRates_VatRateId",
                table: "PurchaseReturnLines");

            migrationBuilder.DropTable(
                name: "VatRates");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturnLines_VatRateId",
                table: "PurchaseReturnLines");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceiptLines_VatRateId",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropIndex(
                name: "IX_FarmingInputItems_PurchaseVatRateId",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(name: "VatPercent", table: "PurchaseReturnLines");
            migrationBuilder.DropColumn(name: "VatRateId", table: "PurchaseReturnLines");
            migrationBuilder.DropColumn(name: "VatRateName", table: "PurchaseReturnLines");
            migrationBuilder.DropColumn(name: "VatPercent", table: "PurchaseReceiptLines");
            migrationBuilder.DropColumn(name: "VatRateId", table: "PurchaseReceiptLines");
            migrationBuilder.DropColumn(name: "VatRateName", table: "PurchaseReceiptLines");
            migrationBuilder.DropColumn(name: "PurchaseVatRateId", table: "FarmingInputItems");
        }
    }
}
