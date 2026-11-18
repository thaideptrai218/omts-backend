using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>Adds "Thời gian trưởng thành" (days from seeding to harvest) to the farming input items; only Seed items fill it.</summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261118100000_SeedItemMaturityDays")]
    public partial class SeedItemMaturityDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaturityDays",
                table: "FarmingInputItems",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaturityDays",
                table: "FarmingInputItems");
        }
    }
}
