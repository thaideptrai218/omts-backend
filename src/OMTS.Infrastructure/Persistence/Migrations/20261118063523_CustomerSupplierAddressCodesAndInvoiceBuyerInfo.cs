using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerSupplierAddressCodesAndInvoiceBuyerInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IdentityNumber",
                table: "Suppliers",
                newName: "PersonalTaxId");

            migrationBuilder.RenameColumn(
                name: "IdentityNumber",
                table: "Customers",
                newName: "PersonalTaxId");

            migrationBuilder.AddColumn<bool>(
                name: "InvoiceAddressSameAsContact",
                table: "Suppliers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerType",
                table: "Suppliers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Individual");

            migrationBuilder.AddColumn<string>(
                name: "InvoiceEmail",
                table: "Suppliers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InvoiceProvinceCode",
                table: "Suppliers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceProvinceName",
                table: "Suppliers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceStreet",
                table: "Suppliers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InvoiceWardCode",
                table: "Suppliers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceWardName",
                table: "Suppliers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvinceCode",
                table: "Suppliers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WardCode",
                table: "Suppliers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InvoiceAddressSameAsContact",
                table: "Customers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceBuyerType",
                table: "Customers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Consumer");

            migrationBuilder.AddColumn<string>(
                name: "InvoiceEmail",
                table: "Customers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InvoiceProvinceCode",
                table: "Customers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceProvinceName",
                table: "Customers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceStreet",
                table: "Customers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InvoiceWardCode",
                table: "Customers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceWardName",
                table: "Customers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvinceCode",
                table: "Customers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WardCode",
                table: "Customers",
                type: "int",
                nullable: true);

            // Whom the e-invoice is made out to, from what the profile already holds. A customer with a tax code or a company name is an
            // Organization, else an Individual with a personal ID number, else a Consumer; a supplier is an Organization with a tax code, else an Individual.
            // The rows keep their column defaults for the invoice address (the contact address) and the invoice name stays in CompanyName.
            migrationBuilder.Sql(
                @"UPDATE [Customers] SET [InvoiceBuyerType] = CASE
                    WHEN NULLIF(LTRIM(RTRIM([TaxCode])), N'') IS NOT NULL OR NULLIF(LTRIM(RTRIM([CompanyName])), N'') IS NOT NULL THEN N'Organization'
                    WHEN NULLIF(LTRIM(RTRIM([PersonalTaxId])), N'') IS NOT NULL THEN N'Individual'
                    ELSE N'Consumer' END");

            migrationBuilder.Sql(
                @"UPDATE [Suppliers] SET [InvoiceBuyerType] = CASE
                    WHEN NULLIF(LTRIM(RTRIM([TaxCode])), N'') IS NOT NULL THEN N'Organization'
                    ELSE N'Individual' END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceAddressSameAsContact",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerType",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "InvoiceEmail",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "InvoiceProvinceCode",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "InvoiceProvinceName",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "InvoiceStreet",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "InvoiceWardCode",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "InvoiceWardName",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "ProvinceCode",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "WardCode",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "InvoiceAddressSameAsContact",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "InvoiceBuyerType",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "InvoiceEmail",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "InvoiceProvinceCode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "InvoiceProvinceName",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "InvoiceStreet",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "InvoiceWardCode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "InvoiceWardName",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ProvinceCode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "WardCode",
                table: "Customers");

            migrationBuilder.RenameColumn(
                name: "PersonalTaxId",
                table: "Suppliers",
                newName: "IdentityNumber");

            migrationBuilder.RenameColumn(
                name: "PersonalTaxId",
                table: "Customers",
                newName: "IdentityNumber");
        }
    }
}
