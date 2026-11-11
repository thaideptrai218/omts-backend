using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stock is the single source of the quantity on hand (PM decision 11/11/2026): a seeding session and a material usage post a use issue (Xuất dùng) with them, linked both ways. The
    /// issue names its source (kind, record, cycle); the session and the usage name their issue. Rows recorded before this stay without a link (legacy): no row is invented for them, as the
    /// ledger would have to be written back in time at costs that were never recorded. Seed from a legacy session is still counted from the session itself.
    /// </summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261111150000_FarmingStockIssueLinks")]
    public partial class FarmingStockIssueLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "CycleId", table: "InputIssues", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<int>(name: "SourceKind", table: "InputIssues", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<Guid>(name: "SourceRecordId", table: "InputIssues", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "IssueId", table: "SeedingRecords", type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "IssueId", table: "MaterialUsages", type: "uniqueidentifier", nullable: true);

            migrationBuilder.CreateIndex(name: "IX_InputIssues_SourceKind_SourceRecordId", table: "InputIssues", columns: new[] { "SourceKind", "SourceRecordId" });
            migrationBuilder.CreateIndex(name: "IX_SeedingRecords_IssueId", table: "SeedingRecords", column: "IssueId");
            migrationBuilder.CreateIndex(name: "IX_MaterialUsages_IssueId", table: "MaterialUsages", column: "IssueId");

            migrationBuilder.AddForeignKey(
                name: "FK_SeedingRecords_InputIssues_IssueId", table: "SeedingRecords", column: "IssueId", principalTable: "InputIssues", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "FK_MaterialUsages_InputIssues_IssueId", table: "MaterialUsages", column: "IssueId", principalTable: "InputIssues", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_SeedingRecords_InputIssues_IssueId", table: "SeedingRecords");
            migrationBuilder.DropForeignKey(name: "FK_MaterialUsages_InputIssues_IssueId", table: "MaterialUsages");
            migrationBuilder.DropIndex(name: "IX_SeedingRecords_IssueId", table: "SeedingRecords");
            migrationBuilder.DropIndex(name: "IX_MaterialUsages_IssueId", table: "MaterialUsages");
            migrationBuilder.DropIndex(name: "IX_InputIssues_SourceKind_SourceRecordId", table: "InputIssues");
            migrationBuilder.DropColumn(name: "IssueId", table: "SeedingRecords");
            migrationBuilder.DropColumn(name: "IssueId", table: "MaterialUsages");
            migrationBuilder.DropColumn(name: "CycleId", table: "InputIssues");
            migrationBuilder.DropColumn(name: "SourceKind", table: "InputIssues");
            migrationBuilder.DropColumn(name: "SourceRecordId", table: "InputIssues");
        }
    }
}
