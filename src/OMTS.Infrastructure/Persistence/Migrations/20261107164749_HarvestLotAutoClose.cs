using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>GFR-05: the marker of the MSG-HARVEST-05 warning on each harvest lot (the close date it announced). Existing lots start with none, so the next check warns them once.</summary>
    public partial class HarvestLotAutoClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "AutoCloseWarnedFor",
                table: "HarvestLots",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCloseWarnedFor",
                table: "HarvestLots");
        }
    }
}
