using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InputVatOnPurchaseReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Lines made before input VAT existed keep no rate (null) and no VAT (0), so every receipt, return and supplier debt keeps its figures.
            migrationBuilder.AddColumn<decimal>(
                name: "VatAmount",
                table: "PurchaseReturnLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "VatRate",
                table: "PurchaseReturnLines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VatAmount",
                table: "PurchaseReceiptLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "VatRate",
                table: "PurchaseReceiptLines",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurchaseVatRate",
                table: "FarmingInputItems",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Kct");

            // Seed items are not subject to VAT (KCT); every other item starts at 10%.
            migrationBuilder.Sql("UPDATE FarmingInputItems SET PurchaseVatRate = 'Ten' WHERE [Group] <> 'Seed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VatAmount",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "VatRate",
                table: "PurchaseReturnLines");

            migrationBuilder.DropColumn(
                name: "VatAmount",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropColumn(
                name: "VatRate",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropColumn(
                name: "PurchaseVatRate",
                table: "FarmingInputItems");
        }
    }
}
