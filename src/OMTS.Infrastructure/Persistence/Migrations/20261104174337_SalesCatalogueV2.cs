using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesCatalogueV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dev and demo data only, and no service wrote products before. The one conversion the new unique name needs:
            // a repeated name takes its old code, the last moment the code exists.
            migrationBuilder.Sql(
                """
                UPDATE p SET [Name] = LEFT(p.[Name], 200 - LEN(p.[Code]) - 3) + N' (' + p.[Code] + N')'
                FROM [Products] p
                WHERE EXISTS (SELECT 1 FROM [Products] o WHERE o.[Name] = p.[Name] AND o.[Id] <> p.[Id]);
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Statuses_StatusId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Products_Code",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Customers_StatusId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SalesUnit",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ValidTo",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "OtherContacts",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ReceivingContact",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "StatusId",
                table: "Customers");

            migrationBuilder.RenameColumn(
                name: "DeliveryAddress",
                table: "Customers",
                newName: "Address");

            // A row that already exists gets 1 VND, so that the check below holds; the Owner sets the real price.
            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                table: "Products",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Customers",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PhoneCheckedByStaff",
                table: "Customers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Name",
                table: "Products",
                column: "Name",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Product_UnitPrice_Positive",
                table: "Products",
                sql: "[UnitPrice] > 0");

            migrationBuilder.CreateIndex(
                name: "UX_Customers_PhoneNumber_CheckedByStaff",
                table: "Customers",
                column: "PhoneNumber",
                unique: true,
                filter: "[PhoneCheckedByStaff] = 1 AND [MergedIntoCustomerId] IS NULL AND [PhoneNumber] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_Name",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Product_UnitPrice_Positive",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "UX_Customers_PhoneNumber_CheckedByStaff",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "UnitPrice",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "PhoneCheckedByStaff",
                table: "Customers");

            migrationBuilder.RenameColumn(
                name: "Address",
                table: "Customers",
                newName: "DeliveryAddress");

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Products",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            // The old unique code: rows made since have none, and their ids are unique.
            migrationBuilder.Sql("UPDATE [Products] SET [Code] = CONVERT(nvarchar(36), [Id]);");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Products",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SalesUnit",
                table: "Products",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "kg");

            migrationBuilder.AddColumn<DateOnly>(
                name: "ValidFrom",
                table: "Products",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ValidTo",
                table: "Products",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OtherContacts",
                table: "Customers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceivingContact",
                table: "Customers",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // 101: Usage In Use, the status every profile had.
            migrationBuilder.AddColumn<int>(
                name: "StatusId",
                table: "Customers",
                type: "int",
                nullable: false,
                defaultValue: 101);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Code",
                table: "Products",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_StatusId",
                table: "Customers",
                column: "StatusId");

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_Statuses_StatusId",
                table: "Customers",
                column: "StatusId",
                principalTable: "Statuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
