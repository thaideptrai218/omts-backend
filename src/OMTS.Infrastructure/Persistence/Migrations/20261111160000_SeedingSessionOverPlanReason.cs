using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A seeding session keeps the reason given when the seed of the cycle went above 110 % of its planned seed quantity and the caller confirmed (decision of 12/11/2026). Null on every
    /// session recorded before, and on a session within the plan.
    /// </summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261111160000_SeedingSessionOverPlanReason")]
    public partial class SeedingSessionOverPlanReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "OverPlanReason", table: "SeedingRecords", type: "nvarchar(1000)", maxLength: 1000, nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "OverPlanReason", table: "SeedingRecords");
        }
    }
}
