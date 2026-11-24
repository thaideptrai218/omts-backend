using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryIndexesForHistoryReportsAndStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InputStockMovements_ItemId_OccurredAt",
                table: "InputStockMovements");

            migrationBuilder.CreateIndex(
                name: "IX_WaterMeasurements_ZoneId_StatusId_MeasuredAt_CreatedAt",
                table: "WaterMeasurements",
                columns: new[] { "ZoneId", "StatusId", "MeasuredAt", "CreatedAt" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationCodes_Channel_LastSentAt",
                table: "VerificationCodes",
                columns: new[] { "Channel", "LastSentAt" })
                .Annotation("SqlServer:Include", new[] { "SendCount" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_CreatedAt",
                table: "SalesReturns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReturns_ReturnDate",
                table: "SalesReturns",
                column: "ReturnDate");

            migrationBuilder.CreateIndex(
                name: "IX_SalesInvoicePriceCuts_CutDate",
                table: "SalesInvoicePriceCuts",
                column: "CutDate");

            migrationBuilder.CreateIndex(
                name: "IX_QualityClaims_SentAt",
                table: "QualityClaims",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Type_Channel_CreatedAt",
                table: "Notifications",
                columns: new[] { "Type", "Channel", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LoginSessions_RefreshTokenExpiresAt",
                table: "LoginSessions",
                column: "RefreshTokenExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_InputStockMovements_ItemId_OccurredAt",
                table: "InputStockMovements",
                columns: new[] { "ItemId", "OccurredAt" })
                .Annotation("SqlServer:Include", new[] { "Quantity", "SeedLotLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_InputStockMovements_ReversesMovementId",
                table: "InputStockMovements",
                column: "ReversesMovementId",
                filter: "[ReversesMovementId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InputIssues_IssuedAt",
                table: "InputIssues",
                column: "IssuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_HarvestLots_WeighedOn",
                table: "HarvestLots",
                column: "WeighedOn");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_PaidAt",
                table: "CustomerPayments",
                column: "PaidAt");

            migrationBuilder.CreateIndex(
                name: "IX_CashVouchers_VoucherDate",
                table: "CashVouchers",
                column: "VoucherDate");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_RecordId_RecordType_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "RecordId", "RecordType", "OccurredAt" },
                filter: "[RecordId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WaterMeasurements_ZoneId_StatusId_MeasuredAt_CreatedAt",
                table: "WaterMeasurements");

            migrationBuilder.DropIndex(
                name: "IX_VerificationCodes_Channel_LastSentAt",
                table: "VerificationCodes");

            migrationBuilder.DropIndex(
                name: "IX_SalesReturns_CreatedAt",
                table: "SalesReturns");

            migrationBuilder.DropIndex(
                name: "IX_SalesReturns_ReturnDate",
                table: "SalesReturns");

            migrationBuilder.DropIndex(
                name: "IX_SalesInvoicePriceCuts_CutDate",
                table: "SalesInvoicePriceCuts");

            migrationBuilder.DropIndex(
                name: "IX_QualityClaims_SentAt",
                table: "QualityClaims");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_Type_Channel_CreatedAt",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_LoginSessions_RefreshTokenExpiresAt",
                table: "LoginSessions");

            migrationBuilder.DropIndex(
                name: "IX_InputStockMovements_ItemId_OccurredAt",
                table: "InputStockMovements");

            migrationBuilder.DropIndex(
                name: "IX_InputStockMovements_ReversesMovementId",
                table: "InputStockMovements");

            migrationBuilder.DropIndex(
                name: "IX_InputIssues_IssuedAt",
                table: "InputIssues");

            migrationBuilder.DropIndex(
                name: "IX_HarvestLots_WeighedOn",
                table: "HarvestLots");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_PaidAt",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CashVouchers_VoucherDate",
                table: "CashVouchers");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_RecordId_RecordType_OccurredAt",
                table: "AuditEvents");

            migrationBuilder.CreateIndex(
                name: "IX_InputStockMovements_ItemId_OccurredAt",
                table: "InputStockMovements",
                columns: new[] { "ItemId", "OccurredAt" });
        }
    }
}
