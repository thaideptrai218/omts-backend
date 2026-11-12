using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Item groups may sit under a parent group; an issue belongs to one raft on its header, so the raft of its lines moves up and the line column goes; and the document codes kept
    /// on the movements are made equal to the codes of the documents they name.
    /// </summary>
    [DbContext(typeof(OmtsDbContext))]
    [Migration("20261111080000_InputGroupParentsIssueHeaderRaftAndMovementCodes")]
    public partial class InputGroupParentsIssueHeaderRaftAndMovementCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "InputItemGroups",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InputItemGroups_ParentId",
                table: "InputItemGroups",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_InputItemGroups_InputItemGroups_ParentId",
                table: "InputItemGroups",
                column: "ParentId",
                principalTable: "InputItemGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // An issue with no raft of its own takes the raft of its first line that has one (the only one, when all lines agree).
            migrationBuilder.Sql(
                """
                UPDATE i SET i.[RaftId] = (
                    SELECT TOP 1 l.[RaftId] FROM [InputIssueLines] l
                    WHERE l.[IssueId] = i.[Id] AND l.[RaftId] IS NOT NULL
                    ORDER BY l.[LineNumber])
                FROM [InputIssues] i
                WHERE i.[RaftId] IS NULL
                  AND EXISTS (SELECT 1 FROM [InputIssueLines] l WHERE l.[IssueId] = i.[Id] AND l.[RaftId] IS NOT NULL);
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_InputIssueLines_Rafts_RaftId",
                table: "InputIssueLines");

            migrationBuilder.DropIndex(
                name: "IX_InputIssueLines_RaftId",
                table: "InputIssueLines");

            migrationBuilder.DropColumn(
                name: "RaftId",
                table: "InputIssueLines");

            // The code of a movement is the code its document has now (an item's opening row names the item).
            migrationBuilder.Sql(
                """
                UPDATE m SET m.[DocumentCode] = r.[Code] FROM [InputStockMovements] m
                INNER JOIN [PurchaseReceipts] r ON r.[Id] = m.[DocumentId] WHERE m.[DocumentCode] <> r.[Code];

                UPDATE m SET m.[DocumentCode] = rt.[Code] FROM [InputStockMovements] m
                INNER JOIN [PurchaseReturns] rt ON rt.[Id] = m.[DocumentId] WHERE m.[DocumentCode] <> rt.[Code];

                UPDATE m SET m.[DocumentCode] = i.[Code] FROM [InputStockMovements] m
                INNER JOIN [InputIssues] i ON i.[Id] = m.[DocumentId] WHERE m.[DocumentCode] <> i.[Code];

                UPDATE m SET m.[DocumentCode] = it.[Code] FROM [InputStockMovements] m
                INNER JOIN [FarmingInputItems] it ON it.[Id] = m.[DocumentId] WHERE m.[Kind] = 'Opening' AND m.[DocumentCode] <> it.[Code];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RaftId",
                table: "InputIssueLines",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE l SET l.[RaftId] = i.[RaftId] FROM [InputIssueLines] l INNER JOIN [InputIssues] i ON i.[Id] = l.[IssueId];
                """);

            migrationBuilder.CreateIndex(
                name: "IX_InputIssueLines_RaftId",
                table: "InputIssueLines",
                column: "RaftId");

            migrationBuilder.AddForeignKey(
                name: "FK_InputIssueLines_Rafts_RaftId",
                table: "InputIssueLines",
                column: "RaftId",
                principalTable: "Rafts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(
                name: "FK_InputItemGroups_InputItemGroups_ParentId",
                table: "InputItemGroups");

            migrationBuilder.DropIndex(
                name: "IX_InputItemGroups_ParentId",
                table: "InputItemGroups");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "InputItemGroups");
        }
    }
}
