using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRaftCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Latitude",
                table: "Rafts",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Longitude",
                table: "Rafts",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Raft_Coordinates",
                table: "Rafts",
                sql: "([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FarmingZone_Coordinates",
                table: "FarmingZones",
                sql: "([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Raft_Coordinates",
                table: "Rafts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FarmingZone_Coordinates",
                table: "FarmingZones");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "Rafts");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "Rafts");
        }
    }
}
