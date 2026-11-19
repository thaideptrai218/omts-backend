using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>Adds the end of a job period (the check-out that closed it), so the gap between two sessions of a day is not paid. Nullable, no backfill: old periods keep their old hours.</summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261119150000_WorkAssignmentEndsAt")]
    public partial class WorkAssignmentEndsAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndsAt",
                table: "WorkAssignments",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "EndsAt", table: "WorkAssignments");
        }
    }
}
