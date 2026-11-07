using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ACCOUNT-07 v2: the JSON holds [{ "Type", "Push" }] instead of category × channel switches. Stored choices are
            // dev and demo data only, so they are reset: every type is pushed again until the user turns it off.
            migrationBuilder.Sql("UPDATE [Accounts] SET [NotificationPreferences] = N'[]' WHERE [NotificationPreferences] IS NOT NULL;");

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "Notifications",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "InApp");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeliveryStatusAt",
                table: "Notifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryStatusId",
                table: "Notifications",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessageCode",
                table: "Notifications",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessageParams",
                table: "Notifications",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                table: "Notifications",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderErrorCode",
                table: "Notifications",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderMessageId",
                table: "Notifications",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RelatedRecordId",
                table: "Notifications",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[,]
                {
                    { 1601, "SENDING", null, "Sending", "MESSAGE_DELIVERY", false, true, "Handed to the SMS Service or Zalo OA, no answer yet.", 1 },
                    { 1602, "SENT", null, "Sent", "MESSAGE_DELIVERY", true, false, "Accepted by the service; this does not mean that the trader has read it.", 2 },
                    { 1603, "NOT_SENT", null, "Not sent", "MESSAGE_DELIVERY", true, false, "Not accepted by the service; nothing that was saved is undone (GFR-06).", 3 }
                });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 53, "System", 1601, "The SMS Service or Zalo OA accepts the message.", false, 1602, "System", "SALE-06, SALE-03, CLAIM-02, TRACE-04" },
                    { 54, "System", 1601, "The SMS Service or Zalo OA does not accept the message.", false, 1603, "System", "SALE-06, SALE-03, CLAIM-02, TRACE-04" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DeliveryStatusId",
                table: "Notifications",
                column: "DeliveryStatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientAccountId_Channel_CreatedAt",
                table: "Notifications",
                columns: new[] { "RecipientAccountId", "Channel", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RelatedRecordType_RelatedRecordId",
                table: "Notifications",
                columns: new[] { "RelatedRecordType", "RelatedRecordId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_Statuses_DeliveryStatusId",
                table: "Notifications",
                column: "DeliveryStatusId",
                principalTable: "Statuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Notifications_Statuses_DeliveryStatusId",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_DeliveryStatusId",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_RecipientAccountId_Channel_CreatedAt",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_RelatedRecordType_RelatedRecordId",
                table: "Notifications");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 53);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 54);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1601);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1602);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1603);

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DeliveryStatusAt",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DeliveryStatusId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "MessageCode",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "MessageParams",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "PhoneNumber",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "ProviderErrorCode",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "ProviderMessageId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "RelatedRecordId",
                table: "Notifications");
        }
    }
}
