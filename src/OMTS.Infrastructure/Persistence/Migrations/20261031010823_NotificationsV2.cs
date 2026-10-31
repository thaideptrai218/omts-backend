using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

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

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 53,
                columns: new[] { "GuardCondition", "UseCases" },
                values: new object[] { "The SMS Service or Zalo OA accepts the message.", "SALE-06, SALE-03, CLAIM-02, TRACE-04" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 54,
                columns: new[] { "GuardCondition", "TriggerEvent", "UseCases" },
                values: new object[] { "The SMS Service or Zalo OA does not accept the message.", "System", "SALE-06, SALE-03, CLAIM-02, TRACE-04" });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1601,
                column: "Meaning",
                value: "Handed to the SMS Service or Zalo OA, no answer yet.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1602,
                column: "Meaning",
                value: "Accepted by the service; this does not mean that the trader has read it.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1603,
                columns: new[] { "Code", "DisplayName", "Meaning" },
                values: new object[] { "NOT_SENT", "Not sent", "Not accepted by the service; nothing that was saved is undone (GFR-06)." });

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

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 53,
                columns: new[] { "GuardCondition", "UseCases" },
                values: new object[] { "Success response from Zalo OA, Email Service or SMS Service.", "UC70" });

            migrationBuilder.UpdateData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 54,
                columns: new[] { "GuardCondition", "TriggerEvent", "UseCases" },
                values: new object[] { "Error response or timeout.", "System; time", "UC70" });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1601,
                column: "Meaning",
                value: "Handed to the channel provider, no response yet.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1602,
                column: "Meaning",
                value: "The provider reports successful delivery.");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 1603,
                columns: new[] { "Code", "DisplayName", "Meaning" },
                values: new object[] { "FAILED", "Failed", "The provider reports an error or the response timeout expires." });
        }
    }
}
