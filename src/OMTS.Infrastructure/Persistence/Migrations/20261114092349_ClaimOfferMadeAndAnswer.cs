using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClaimOfferMadeAndAnswer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_QualityClaims_Invoice_Awaiting",
                table: "QualityClaims");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QualityClaim_Accepted_Has_Offer",
                table: "QualityClaims");

            migrationBuilder.AddColumn<string>(
                name: "AnswerNote",
                table: "QualityClaims",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AnsweredAt",
                table: "QualityClaims",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OfferAmount",
                table: "QualityClaims",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 220,
                columns: new[] { "GuardCondition", "ToStatusId" },
                values: new object[] { "The invoice is not cancelled and has no sales return; an offer is chosen, a price cut has its amount, and a note is optional.", 3704 });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3703,
                column: "Meaning",
                value: "The trader accepted the offer of the Cooperative Owner (a return of all the goods or a price cut of the whole invoice).");

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 3704, "OFFER_MADE", null, "Offer made", "QUALITY_CLAIM", false, false, "The Cooperative Owner proposed a return of all the goods or a price cut of the whole invoice; the trader has not answered yet.", 4 },
                    { 3705, "OFFER_DECLINED", null, "Offer declined", "QUALITY_CLAIM", true, false, "The trader declined the offer of the Cooperative Owner.", 5 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 222, "Trader", 3704, "The trader of the claim accepts the offer; the Owner then runs the return (CLAIM-03) or the price cut (SALE-07).", false, 3703, "Action", "CLAIM-02" },
                    { 223, "Trader", 3704, "The trader of the claim declines the offer; a note is optional.", false, 3705, "Action", "CLAIM-02" }
                });

            // A claim the Owner had accepted before is now an offer waiting for the trader (PM answer 12/11: the Owner proposes, the trader accepts or declines).
            migrationBuilder.Sql("UPDATE [QualityClaims] SET [StatusId] = 3704 WHERE [StatusId] = 3703");

            migrationBuilder.CreateIndex(
                name: "UX_QualityClaims_Invoice_Awaiting",
                table: "QualityClaims",
                column: "InvoiceId",
                unique: true,
                filter: "[StatusId] IN (3701, 3704)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_QualityClaim_Answered_By_Trader",
                table: "QualityClaims",
                sql: "[StatusId] NOT IN (3703, 3705) OR [AnsweredAt] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_QualityClaim_OfferAmount",
                table: "QualityClaims",
                sql: "[OfferAmount] IS NULL OR [OfferAmount] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_QualityClaim_Offered_Has_Offer",
                table: "QualityClaims",
                sql: "[StatusId] NOT IN (3704, 3703, 3705) OR [Offer] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_QualityClaims_Invoice_Awaiting",
                table: "QualityClaims");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QualityClaim_Answered_By_Trader",
                table: "QualityClaims");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QualityClaim_OfferAmount",
                table: "QualityClaims");

            migrationBuilder.DropCheckConstraint(
                name: "CK_QualityClaim_Offered_Has_Offer",
                table: "QualityClaims");

            // Down: an offer made, accepted or declined by the trader is again a claim accepted by the Owner (the trader's answer is lost).
            migrationBuilder.Sql("UPDATE [QualityClaims] SET [StatusId] = 3703 WHERE [StatusId] IN (3704, 3705)");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 222);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 223);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3704);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3705);

            migrationBuilder.DropColumn(
                name: "AnswerNote",
                table: "QualityClaims");

            migrationBuilder.DropColumn(
                name: "AnsweredAt",
                table: "QualityClaims");

            migrationBuilder.DropColumn(
                name: "OfferAmount",
                table: "QualityClaims");

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 220,
                columns: new[] { "GuardCondition", "ToStatusId" },
                values: new object[] { "The invoice is not cancelled and has no sales return; an offer is chosen and a note is optional.", 3703 });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3703,
                column: "Meaning",
                value: "Accepted by the Cooperative Owner, who offered a return of all the goods or a price cut of the whole invoice.");

            migrationBuilder.CreateIndex(
                name: "UX_QualityClaims_Invoice_Awaiting",
                table: "QualityClaims",
                column: "InvoiceId",
                unique: true,
                filter: "[StatusId] = 3701");

            migrationBuilder.AddCheckConstraint(
                name: "CK_QualityClaim_Accepted_Has_Offer",
                table: "QualityClaims",
                sql: "[StatusId] <> 3703 OR [Offer] IS NOT NULL");
        }
    }
}
