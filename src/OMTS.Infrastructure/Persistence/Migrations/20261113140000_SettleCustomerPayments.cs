using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SettleCustomerPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PaidBackForPaymentId",
                table: "CustomerPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[] { 213, "Accountant; Cooperative Owner", 3602, "The money paid back that settled a payment without a customer was cancelled, so the payment is settled again.", false, 3601, "Action", "DEBT-02.4, DEBT-02.6" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_PaidBackForPayment",
                table: "CustomerPayments",
                column: "PaidBackForPaymentId",
                unique: true,
                filter: "[PaidBackForPaymentId] IS NOT NULL AND [StatusId] <> 3603");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_CustomerPayments_PaidBackForPaymentId",
                table: "CustomerPayments",
                column: "PaidBackForPaymentId",
                principalTable: "CustomerPayments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_CustomerPayments_PaidBackForPaymentId",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_PaidBackForPayment",
                table: "CustomerPayments");

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyColumnType: "int",
                keyValue: 213);

            migrationBuilder.DropColumn(
                name: "PaidBackForPaymentId",
                table: "CustomerPayments");
        }
    }
}
