using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerProfilePaymentsAllocationsAndAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CustomerPayment_Amount",
                table: "CustomerPayments");

            migrationBuilder.AddColumn<string>(
                name: "BankAccountNumber",
                table: "Customers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankBin",
                table: "Customers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "Customers",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BirthDate",
                table: "Customers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Customers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "Customers",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerType",
                table: "Customers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Individual");

            migrationBuilder.AddColumn<string>(
                name: "Facebook",
                table: "Customers",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "Customers",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityNumber",
                table: "Customers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber2",
                table: "Customers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoUrl",
                table: "Customers",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Province",
                table: "Customers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatusId",
                table: "Customers",
                type: "int",
                nullable: false,
                defaultValue: 101);

            migrationBuilder.AddColumn<string>(
                name: "TaxCode",
                table: "Customers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ward",
                table: "Customers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CashAccountId",
                table: "CustomerPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "CustomerPayments",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidAt",
                table: "CustomerPayments",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));


            // Every existing profile gets its code, oldest first (KH000001…), and the KH sequence goes on from there, so a generated code never repeats one of them.
            migrationBuilder.Sql(@"
WITH numbered AS (SELECT [Id], ROW_NUMBER() OVER (ORDER BY [CreatedAt], [Id]) AS [Number] FROM [Customers])
UPDATE c SET c.[Code] = N'KH' + REPLICATE(N'0', CASE WHEN 6 - LEN(CAST(n.[Number] AS nvarchar(20))) > 0 THEN 6 - LEN(CAST(n.[Number] AS nvarchar(20))) ELSE 0 END) + CAST(n.[Number] AS nvarchar(20))
FROM [Customers] c INNER JOIN numbered n ON n.[Id] = c.[Id];");
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM [Customers]) AND NOT EXISTS (SELECT 1 FROM [DocumentSequences] WHERE [Key] = N'KH')
    INSERT INTO [DocumentSequences] ([Key], [LastValue]) VALUES (N'KH', (SELECT COUNT_BIG(*) FROM [Customers]));");

            // A payment recorded before the time was kept happened on its date, at the start of that day in Vietnam (UTC+7).
            migrationBuilder.Sql(@"UPDATE [CustomerPayments] SET [PaidAt] = TODATETIMEOFFSET(CAST([PaymentDate] AS datetime2), N'+07:00');");

            migrationBuilder.CreateTable(
                name: "CustomerDebtAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AdjustmentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerDebtAdjustments", x => x.Id);
                    table.CheckConstraint("CK_CustomerDebtAdjustment_Amount", "[Amount] <> 0");
                    table.ForeignKey(
                        name: "FK_CustomerDebtAdjustments_Accounts_CancelledByAccountId",
                        column: x => x.CancelledByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerDebtAdjustments_Accounts_RecordedByAccountId",
                        column: x => x.RecordedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerDebtAdjustments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPaymentAllocations", x => x.Id);
                    table.CheckConstraint("CK_CustomerPaymentAllocation_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_CustomerPayments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "CustomerPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentAllocations_SalesInvoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "SalesInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "CashVoucherTypes",
                columns: new[] { "Id", "Category", "CountsInResult", "CreatedAt", "CreatedBy", "Description", "Direction", "IsActive", "IsSystem", "Manual", "Name", "UpdatedAt", "UpdatedBy" },
                values: new object[] { new Guid("5d6c1a10-0000-4c3a-9a10-000000000008"), "Goods", false, new DateTimeOffset(new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, null, "Payment", true, true, false, "Chi trả lại khách", null, null });

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3602,
                columns: new[] { "Code", "DisplayName", "Meaning" },
                values: new object[] { "COMPLETED", "Completed", "The money has arrived or was recorded by the Accountant; counts in the debt." });

            migrationBuilder.InsertData(
                table: "Statuses",
                columns: new[] { "Id", "Code", "DisplayColor", "DisplayName", "Group", "IsFinal", "IsInitial", "Meaning", "SortOrder" },
                values: new object[] { 3604, "PENDING_TRANSFER", null, "Pending transfer", "CUSTOMER_PAYMENT", false, true, "A VietQR code was made for the payment and the transfer has not arrived; counts nowhere until it does.", 4 });

            migrationBuilder.InsertData(
                table: "StatusTransitions",
                columns: new[] { "Id", "AllowedPerformers", "FromStatusId", "GuardCondition", "ReasonRequired", "ToStatusId", "TriggerEvent", "UseCases" },
                values: new object[,]
                {
                    { 30078, "System; Accountant", 3604, "The transfer with the payment code arrived; the payment takes the amount received.", false, 3602, "Action", "DEBT-02, GFR-08" },
                    { 30079, "Accountant; Cooperative Owner", 3604, "The customer did not transfer, or the code was made by mistake.", true, 3603, "Action", "DEBT-02" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Code",
                table: "Customers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_StatusId",
                table: "Customers",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_CashAccountId",
                table: "CustomerPayments",
                column: "CashAccountId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomerPayment_Amount",
                table: "CustomerPayments",
                sql: "[Amount] >= 0 AND [DiscountAmount] >= 0 AND ([Amount] > 0 OR [DiscountAmount] > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDebtAdjustments_CancelledByAccountId",
                table: "CustomerDebtAdjustments",
                column: "CancelledByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDebtAdjustments_Code",
                table: "CustomerDebtAdjustments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDebtAdjustments_CustomerId_AdjustmentDate",
                table: "CustomerDebtAdjustments",
                columns: new[] { "CustomerId", "AdjustmentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerDebtAdjustments_RecordedByAccountId",
                table: "CustomerDebtAdjustments",
                column: "RecordedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocations_InvoiceId",
                table: "CustomerPaymentAllocations",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentAllocations_PaymentId_InvoiceId",
                table: "CustomerPaymentAllocations",
                columns: new[] { "PaymentId", "InvoiceId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_CashAccounts_CashAccountId",
                table: "CustomerPayments",
                column: "CashAccountId",
                principalTable: "CashAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_Statuses_StatusId",
                table: "Customers",
                column: "StatusId",
                principalTable: "Statuses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_CashAccounts_CashAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Statuses_StatusId",
                table: "Customers");

            migrationBuilder.DropTable(
                name: "CustomerDebtAdjustments");

            migrationBuilder.DropTable(
                name: "CustomerPaymentAllocations");

            migrationBuilder.DropIndex(
                name: "IX_Customers_Code",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_StatusId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_CashAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustomerPayment_Amount",
                table: "CustomerPayments");

            migrationBuilder.DeleteData(
                table: "CashVoucherTypes",
                keyColumn: "Id",
                keyValue: new Guid("5d6c1a10-0000-4c3a-9a10-000000000008"));

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30078);

            migrationBuilder.DeleteData(
                table: "StatusTransitions",
                keyColumn: "Id",
                keyValue: 30079);

            migrationBuilder.DeleteData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3604);

            migrationBuilder.DropColumn(
                name: "BankAccountNumber",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "BankBin",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "BirthDate",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "CustomerType",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Facebook",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "IdentityNumber",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "PhoneNumber2",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "PhotoUrl",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Province",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "StatusId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TaxCode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Ward",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "CashAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "CustomerPayments");

            migrationBuilder.UpdateData(
                table: "Statuses",
                keyColumn: "Id",
                keyValue: 3602,
                columns: new[] { "Code", "DisplayName", "Meaning" },
                values: new object[] { "CONFIRMED", "Confirmed", "Checked or recorded by the Accountant." });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustomerPayment_Amount",
                table: "CustomerPayments",
                sql: "[Amount] > 0");
        }
    }
}
