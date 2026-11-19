using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>Adds the commercial size (shell length in cm) to the products: SizeMinCm alone is an exact size, with SizeMaxCm a range.</summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261118120000_ProductSizeCm")]
    public partial class ProductSizeCm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SizeMaxCm",
                table: "Products",
                type: "decimal(4,1)",
                precision: 4,
                scale: 1,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SizeMinCm",
                table: "Products",
                type: "decimal(4,1)",
                precision: 4,
                scale: 1,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SizeMaxCm", table: "Products");
            migrationBuilder.DropColumn(name: "SizeMinCm", table: "Products");
        }
    }
}
