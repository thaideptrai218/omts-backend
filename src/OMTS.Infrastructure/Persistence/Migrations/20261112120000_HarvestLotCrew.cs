using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The crew of a harvest lot (decision of 12/11/2026): the employees who worked on the trip, optional, for the record only (no quota per person, no link to orders). One employee may be on the
    /// lots of several rafts the same day. Existing lots have no crew. Down drops the table.
    /// </summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261112120000_HarvestLotCrew")]
    public partial class HarvestLotCrew : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HarvestLotCrew",
                columns: table => new
                {
                    LotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarvestLotCrew", x => new { x.LotId, x.EmployeeId });
                    table.ForeignKey(
                        name: "FK_HarvestLotCrew_Employees_EmployeeId", column: x => x.EmployeeId, principalTable: "Employees", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HarvestLotCrew_HarvestLots_LotId", column: x => x.LotId, principalTable: "HarvestLots", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(name: "IX_HarvestLotCrew_EmployeeId", table: "HarvestLotCrew", column: "EmployeeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HarvestLotCrew");
        }
    }
}
