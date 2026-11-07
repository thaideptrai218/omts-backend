using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PR H1b, HARVEST-01.1: a removed lot code is not given again. The <c>HarvestLot.Created</c> audit rows outlive their lots, so they hold every code ever given, and this index makes
    /// each code one row: a save that read the codes before another raft took the code and removed the lot again fails on it and takes the next code. No row changes. On a database with two
    /// Created rows of one code the Up fails: only a Down below HarvestLots can make them, as it cuts codes to 100 characters, and data is dev/demo only.
    /// </summary>
    public partial class HarvestLotCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_AuditEvents_HarvestLotCreatedCode",
                table: "AuditEvents",
                column: "RecordCode",
                unique: true,
                filter: "[Action] = 'HarvestLot.Created' AND [RecordCode] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AuditEvents_HarvestLotCreatedCode",
                table: "AuditEvents");
        }
    }
}
