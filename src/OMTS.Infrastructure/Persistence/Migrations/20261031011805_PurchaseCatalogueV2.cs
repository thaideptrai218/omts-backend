using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PURCHASE-07/08 of SRS 27/10: supplier address and email (the free-text other contacts become the address), unique
    /// item names, and the item group RaftMaterial renamed Material (enums are stored by name). Demo and test data are not
    /// converted further; the unique name index fails on a database where two items share a name (reset it).
    /// </summary>
    public partial class PurchaseCatalogueV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only rows still on the old name: running it on a database already converted changes nothing.
            migrationBuilder.Sql("UPDATE [FarmingInputItems] SET [Group] = N'Material' WHERE [Group] = N'RaftMaterial';");

            migrationBuilder.RenameColumn(
                name: "OtherContacts",
                table: "Suppliers",
                newName: "Address");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Suppliers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FarmingInputItems_Name",
                table: "FarmingInputItems",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FarmingInputItems_Name",
                table: "FarmingInputItems");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Suppliers");

            migrationBuilder.RenameColumn(
                name: "Address",
                table: "Suppliers",
                newName: "OtherContacts");

            migrationBuilder.Sql("UPDATE [FarmingInputItems] SET [Group] = N'RaftMaterial' WHERE [Group] = N'Material';");
        }
    }
}
