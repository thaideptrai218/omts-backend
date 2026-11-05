using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PURCHASE-01/03/06 of SRS 27/10 (OMTS-218 P2): a purchase receipt has no zone, no discount, no paid-now amount and no
    /// invoice photo; each Seed or Material line may name a raft and every line has a number; a seed lot is the Seed line
    /// itself (PN000123-1), so SeedReceiptLots is dropped and the seeding events point to the receipt line; line amounts are
    /// no longer whole thousands; a Draft may hold incomplete lines and is removed, never cancelled (transition 71 goes).
    /// Data is dev/demo only: existing rows are kept as they are, except what the new schema needs (line numbers, the raft of
    /// the receipt copied to its Seed and Material lines, a date on every Draft, the seeding events re-pointed). Old receipt
    /// codes (PR-yyyyMMdd-nnn) stay; new receipts take PN codes. Down is lossy (discounts, paid-now methods, photos, zones).
    /// </summary>
    public partial class PurchaseReceiptsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReceipts_FarmingZones_ZoneId",
                table: "PurchaseReceipts");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReceipts_PurchaseReceipts_CopiedFromReceiptId",
                table: "PurchaseReceipts");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReceipts_Rafts_RaftId",
                table: "PurchaseReceipts");

            migrationBuilder.DropForeignKey(
                name: "FK_SeedingEvents_SeedReceiptLots_SeedLotId",
                table: "SeedingEvents");

            // A seeding event now points to the seed line itself (GBR-04).
            migrationBuilder.Sql(
                "UPDATE e SET e.SeedLotId = l.PurchaseReceiptLineId FROM SeedingEvents e JOIN SeedReceiptLots l ON l.Id = e.SeedLotId;");

            migrationBuilder.DropTable(
                name: "SeedReceiptLots");

            migrationBuilder.AddColumn<int>(
                name: "LineNumber",
                table: "PurchaseReceiptLines",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RaftId",
                table: "PurchaseReceiptLines",
                type: "uniqueidentifier",
                nullable: true);

            // Lines numbered 1…n per receipt, in the order the detail showed them (item code); the receipt raft goes on its
            // Seed and Material lines; a Draft without a date takes the day it was created (Vietnam time).
            migrationBuilder.Sql(
                """
                UPDATE l SET l.LineNumber = n.Number
                FROM PurchaseReceiptLines l
                JOIN (SELECT pl.Id, ROW_NUMBER() OVER (PARTITION BY pl.ReceiptId ORDER BY i.Code, pl.Id) AS Number
                      FROM PurchaseReceiptLines pl JOIN FarmingInputItems i ON i.Id = pl.ItemId) n ON n.Id = l.Id;
                """);
            migrationBuilder.Sql(
                """
                UPDATE l SET l.RaftId = r.RaftId
                FROM PurchaseReceiptLines l
                JOIN PurchaseReceipts r ON r.Id = l.ReceiptId
                JOIN FarmingInputItems i ON i.Id = l.ItemId
                WHERE r.RaftId IS NOT NULL AND i.[Group] IN (N'Seed', N'Material');
                """);
            migrationBuilder.Sql(
                "UPDATE PurchaseReceipts SET ReceiptDate = CAST(SWITCHOFFSET(CreatedAt, '+07:00') AS date) WHERE ReceiptDate = '0001-01-01';");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceipts_CopiedFromReceiptId",
                table: "PurchaseReceipts");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceipts_RaftId",
                table: "PurchaseReceipts");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceipts_ZoneId_ReceiptDate",
                table: "PurchaseReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseReceipt_AmountPaidNow_Thousands",
                table: "PurchaseReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseReceipt_DiscountAmount_Thousands",
                table: "PurchaseReceipts");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceiptLines_ReceiptId",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseReceiptLine_LineAmount_Thousands",
                table: "PurchaseReceiptLines");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 71);

            migrationBuilder.DropColumn(
                name: "AmountPaidNow",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "CopiedFromReceiptId",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "InvoicePhotoUrls",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "PaidNowMethod",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "RaftId",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "PurchaseReceipts");

            migrationBuilder.DropColumn(
                name: "ActualSource",
                table: "PurchaseReceiptLines");

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitPrice",
                table: "PurchaseReceiptLines",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,0)",
                oldPrecision: 18);

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "PurchaseReceiptLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<Guid>(
                name: "ItemId",
                table: "PurchaseReceiptLines",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 70,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Zone Manager (own drafts); Cooperative Owner", "Supplier In Use, date received not later than today, and every line complete with an input item In Use, a quantity greater than 0 and a unit price of 0 or more.", "PURCHASE-01" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 72,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Zone Manager (own receipts); Cooperative Owner", "No seed lot of the receipt has been seeded and the receipt has no purchase return that is not cancelled.", "PURCHASE-06" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 73,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Accountant; Cooperative Owner", "A payment recorded by mistake; payments are never cancelled with a purchase receipt.", "PURCHASE-05" });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1701,
                column: "Meaning",
                value: "Being prepared; counts nowhere and creates no seed lot. Removed, never cancelled.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1702,
                column: "Meaning",
                value: "Cannot be changed; counts in the supplier debt and raft material costs; each Seed line is a seed lot.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1703,
                column: "Meaning",
                value: "Kept with its reason; counts nowhere; its Seed lines are no longer seed lots.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1802,
                column: "Meaning",
                value: "Recorded by mistake; no longer counts in the supplier debt.");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_ReceiptDate_StatusId",
                table: "PurchaseReceipts",
                columns: new[] { "ReceiptDate", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceiptLines_RaftId",
                table: "PurchaseReceiptLines",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceiptLines_ReceiptId_LineNumber",
                table: "PurchaseReceiptLines",
                columns: new[] { "ReceiptId", "LineNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReceiptLines_Rafts_RaftId",
                table: "PurchaseReceiptLines",
                column: "RaftId",
                principalTable: "Rafts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeedingEvents_PurchaseReceiptLines_SeedLotId",
                table: "SeedingEvents",
                column: "SeedLotId",
                principalTable: "PurchaseReceiptLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // One counter per document kind (PN now; HD, payments and wage vouchers later): a number is taken once and
            // never given again, even when its Draft is removed.
            migrationBuilder.CreateTable(
                name: "DocumentSequences",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LastValue = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSequences", x => x.Key);
                });

            // The PN counter starts after the highest PN code already given (none on most databases).
            migrationBuilder.Sql(
                """
                INSERT INTO DocumentSequences ([Key], LastValue)
                SELECT N'PN', ISNULL(MAX(TRY_CAST(SUBSTRING(Code, 3, 20) AS bigint)), 0)
                FROM PurchaseReceipts
                WHERE Code LIKE N'PN[0-9]%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentSequences");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseReceiptLines_Rafts_RaftId",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropForeignKey(
                name: "FK_SeedingEvents_PurchaseReceiptLines_SeedLotId",
                table: "SeedingEvents");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceipts_ReceiptDate_StatusId",
                table: "PurchaseReceipts");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceiptLines_RaftId",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReceiptLines_ReceiptId_LineNumber",
                table: "PurchaseReceiptLines");

            migrationBuilder.CreateTable(
                name: "SeedReceiptLots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseReceiptLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeedTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActualSource = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceUrls = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InternalCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    SupplierLotCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeedReceiptLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeedReceiptLots_FarmingInputItems_SeedTypeId",
                        column: x => x.SeedTypeId,
                        principalTable: "FarmingInputItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedReceiptLots_PurchaseReceiptLines_PurchaseReceiptLineId",
                        column: x => x.PurchaseReceiptLineId,
                        principalTable: "PurchaseReceiptLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeedReceiptLots_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Lossy way back: a lot (with the id of its line, so the seeding events keep pointing to it) for every seed line
            // of a Completed receipt; the old lot codes counted the seed lines only.
            migrationBuilder.Sql(
                """
                INSERT INTO SeedReceiptLots (Id, PurchaseReceiptLineId, SeedTypeId, SupplierId, InternalCode, Quantity, Unit, ReceivedAt, EvidenceUrls, CreatedAt)
                SELECT l.Id, l.Id, l.ItemId, r.SupplierId, r.Code + N'-S' + CAST(ROW_NUMBER() OVER (PARTITION BY l.ReceiptId ORDER BY l.LineNumber) AS nvarchar(10)),
                       ISNULL(l.Quantity, 0), l.Unit, TODATETIMEOFFSET(CAST(r.ReceiptDate AS datetime2), '+07:00'), N'[]', SYSDATETIMEOFFSET()
                FROM PurchaseReceiptLines l
                JOIN PurchaseReceipts r ON r.Id = l.ReceiptId
                JOIN FarmingInputItems i ON i.Id = l.ItemId
                WHERE r.StatusId = 1702 AND i.[Group] = N'Seed';
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "RaftId",
                table: "PurchaseReceipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                table: "PurchaseReceipts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // A receipt takes the raft of its first line that names one, and the zone of that raft (else the first zone).
            migrationBuilder.Sql(
                """
                UPDATE r SET r.RaftId = (SELECT TOP 1 l.RaftId FROM PurchaseReceiptLines l WHERE l.ReceiptId = r.Id AND l.RaftId IS NOT NULL ORDER BY l.LineNumber)
                FROM PurchaseReceipts r;
                UPDATE r SET r.ZoneId = COALESCE(
                    (SELECT f.ZoneId FROM Rafts f WHERE f.Id = r.RaftId),
                    (SELECT TOP 1 z.Id FROM FarmingZones z ORDER BY z.CreatedAt, z.Id))
                FROM PurchaseReceipts r;
                DELETE FROM PurchaseReceiptLines WHERE ItemId IS NULL;
                UPDATE PurchaseReceiptLines SET LineAmount = ROUND(LineAmount, -3);
                """);

            migrationBuilder.DropColumn(
                name: "LineNumber",
                table: "PurchaseReceiptLines");

            migrationBuilder.DropColumn(
                name: "RaftId",
                table: "PurchaseReceiptLines");

            migrationBuilder.AddColumn<decimal>(
                name: "AmountPaidNow",
                table: "PurchaseReceipts",
                type: "decimal(18,0)",
                precision: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "CopiedFromReceiptId",
                table: "PurchaseReceipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "PurchaseReceipts",
                type: "decimal(18,0)",
                precision: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "InvoicePhotoUrls",
                table: "PurchaseReceipts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PaidNowMethod",
                table: "PurchaseReceipts",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitPrice",
                table: "PurchaseReceiptLines",
                type: "decimal(18,0)",
                precision: 18,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,0)",
                oldPrecision: 18,
                oldScale: 0,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "PurchaseReceiptLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,3)",
                oldPrecision: 18,
                oldScale: 3,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ItemId",
                table: "PurchaseReceiptLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActualSource",
                table: "PurchaseReceiptLines",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 70,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner; Zone Manager", "Full validation of supplier, zone, raft, lines and amounts passes.", "INP-03" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 72,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner; Zone Manager", "No seed lot of the receipt is stocked and the receipt has no purchase return.", "INP-04" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 73,
                columns: new[] { "AllowedPerformers", "GuardCondition", "UseCases" },
                values: new object[] { "Cooperative Owner; Zone Manager", "The receipt is cancelled together with its payments.", "INP-04" });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 71, "Cooperative Owner; Zone Manager", 1701, "Cancel a draft that will not be completed.", true, 1703, "Action", "INP-04" });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1701,
                column: "Meaning",
                value: "Being recorded; does not count in costs, supplier debt or seed lots.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1702,
                column: "Meaning",
                value: "Counts in input costs and supplier debt; seed lots created; never edited.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1703,
                column: "Meaning",
                value: "Recorded by mistake; leaves costs and supplier debt, its seed lots cannot be stocked.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1802,
                column: "Meaning",
                value: "Cancelled together with its purchase receipt.");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_CopiedFromReceiptId",
                table: "PurchaseReceipts",
                column: "CopiedFromReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_RaftId",
                table: "PurchaseReceipts",
                column: "RaftId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceipts_ZoneId_ReceiptDate",
                table: "PurchaseReceipts",
                columns: new[] { "ZoneId", "ReceiptDate" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseReceipt_AmountPaidNow_Thousands",
                table: "PurchaseReceipts",
                sql: "[AmountPaidNow] % 1000 = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseReceipt_DiscountAmount_Thousands",
                table: "PurchaseReceipts",
                sql: "[DiscountAmount] % 1000 = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReceiptLines_ReceiptId",
                table: "PurchaseReceiptLines",
                column: "ReceiptId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseReceiptLine_LineAmount_Thousands",
                table: "PurchaseReceiptLines",
                sql: "[LineAmount] % 1000 = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SeedReceiptLots_InternalCode",
                table: "SeedReceiptLots",
                column: "InternalCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeedReceiptLots_PurchaseReceiptLineId",
                table: "SeedReceiptLots",
                column: "PurchaseReceiptLineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeedReceiptLots_SeedTypeId",
                table: "SeedReceiptLots",
                column: "SeedTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SeedReceiptLots_SupplierId",
                table: "SeedReceiptLots",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReceipts_FarmingZones_ZoneId",
                table: "PurchaseReceipts",
                column: "ZoneId",
                principalTable: "FarmingZones",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReceipts_PurchaseReceipts_CopiedFromReceiptId",
                table: "PurchaseReceipts",
                column: "CopiedFromReceiptId",
                principalTable: "PurchaseReceipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseReceipts_Rafts_RaftId",
                table: "PurchaseReceipts",
                column: "RaftId",
                principalTable: "Rafts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SeedingEvents_SeedReceiptLots_SeedLotId",
                table: "SeedingEvents",
                column: "SeedLotId",
                principalTable: "SeedReceiptLots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
