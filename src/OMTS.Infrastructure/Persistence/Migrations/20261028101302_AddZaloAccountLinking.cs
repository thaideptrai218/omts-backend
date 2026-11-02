using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddZaloAccountLinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessRequestNote",
                table: "Accounts",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AccessRequestedAt",
                table: "Accounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "Accounts",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZaloAccountId",
                table: "Accounts",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 303, "PENDING_APPROVAL", null, "Pending approval", "ACCOUNT", false, true, "A trader asked for an account from the Zalo Mini App; cannot log in until the Cooperative Owner approves.", 3 },
                    { 304, "REJECTED", null, "Rejected", "ACCOUNT", false, false, "The Cooperative Owner refused the request; the trader may ask again.", 4 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 74, "Cooperative Owner", 303, "Approve the trader's request.", false, 301, "Action", "ACS-01 (Zalo)" },
                    { 75, "Cooperative Owner", 303, "Refuse the trader's request.", true, 304, "Action", "ACS-01 (Zalo)" },
                    { 76, "Trader", 304, "The trader asks again from the Zalo Mini App.", false, 303, "Action", "ACS-01 (Zalo)" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_ZaloAccountId",
                table: "Accounts",
                column: "ZaloAccountId",
                unique: true,
                filter: "[ZaloAccountId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_ZaloAccountId",
                table: "Accounts");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 74);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 75);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 76);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 303);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 304);

            migrationBuilder.DropColumn(
                name: "AccessRequestNote",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AccessRequestedAt",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ZaloAccountId",
                table: "Accounts");
        }
    }
}
