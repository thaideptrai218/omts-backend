using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseReceiptKindSeedOrSupply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "PurchaseReceipts",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Supply");

            // A receipt with a Seed line is a Seed receipt, every other one (Material and Other items, or no item yet on a Draft) a Supply receipt. A receipt that mixes seed and other items has no right kind:
            // it becomes a Seed receipt and keeps its other lines. The count of such receipts is printed so the operator can look at them; none existed in the development data (0 of 26).
            migrationBuilder.Sql(@"
UPDATE r SET Kind = 'Seed'
FROM PurchaseReceipts r
WHERE EXISTS (SELECT 1 FROM PurchaseReceiptLines l JOIN FarmingInputItems i ON i.Id = l.ItemId WHERE l.ReceiptId = r.Id AND i.[Group] = 'Seed');

DECLARE @mixed int = (
    SELECT COUNT(*) FROM PurchaseReceipts r
    WHERE r.Kind = 'Seed'
      AND EXISTS (SELECT 1 FROM PurchaseReceiptLines l JOIN FarmingInputItems i ON i.Id = l.ItemId WHERE l.ReceiptId = r.Id AND i.[Group] <> 'Seed'));
PRINT CONCAT('Purchase receipts that mix seed and other items, classified as Seed: ', @mixed);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PurchaseReceipts");
        }
    }
}
