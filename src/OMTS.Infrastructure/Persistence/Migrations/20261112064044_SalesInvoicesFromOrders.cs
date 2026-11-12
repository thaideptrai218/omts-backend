using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesInvoicesFromOrders : Migration
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

            // Every existing line was weighed and priced in kg: it takes the base unit of its product (quantity = kg, factor 1), so its unit price keeps its meaning. A product with no base unit
            // (none is expected: the catalogue migration gave every product one) falls back to the shared kg unit.
            migrationBuilder.Sql(
                """
                UPDATE l SET l.UnitId = pu.UnitId, l.UnitCode = pu.Code, l.UnitName = iu.Name, l.Factor = 1, l.Quantity = l.Kg
                FROM SalesInvoiceLines l
                JOIN ProductUnits pu ON pu.ProductId = l.ProductId AND pu.IsBase = 1
                JOIN InputUnits iu ON iu.Id = pu.UnitId;

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
            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoiceLines_SalesOrderLines_SalesOrderLineId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesInvoices_SalesOrders_SalesOrderId",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoices_SalesOrderId",
                table: "SalesInvoices");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoiceLines_SalesOrderLineId",
                table: "SalesInvoiceLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SalesInvoiceLine_Quantity",
                table: "SalesInvoiceLines");

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
        }
    }
}
