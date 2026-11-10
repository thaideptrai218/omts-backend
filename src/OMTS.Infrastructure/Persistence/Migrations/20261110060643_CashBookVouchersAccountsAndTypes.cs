using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CashBookVouchersAccountsAndTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CashAccountId",
                table: "SupplierPayments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CashAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    BankBin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ProviderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AccountNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    HolderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashParties",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Area = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Ward = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashParties", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashVoucherTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CountsInResult = table.Column<bool>(type: "bit", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    Manual = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashVoucherTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashVouchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Fund = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CashAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    VoucherDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    CollectedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PartyKind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PartyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CountsInResult = table.Column<bool>(type: "bit", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashVouchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashVouchers_Accounts_CollectedByAccountId",
                        column: x => x.CollectedByAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashVouchers_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashVouchers_CashVoucherTypes_TypeId",
                        column: x => x.TypeId,
                        principalTable: "CashVoucherTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "CashVoucherTypes",
                columns: new[] { "Id", "Category", "CountsInResult", "CreatedAt", "CreatedBy", "Description", "Direction", "IsActive", "IsSystem", "Manual", "Name", "UpdatedAt", "UpdatedBy" },
                values: new object[,]
                {
                    { new Guid("5d6c1a10-0000-4c3a-9a10-000000000001"), "Goods", false, new DateTimeOffset(new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, null, "Receipt", true, true, false, "Thu tiền khách trả", null, null },
                    { new Guid("5d6c1a10-0000-4c3a-9a10-000000000002"), "Goods", false, new DateTimeOffset(new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, null, "Receipt", true, true, false, "Thu tiền NCC hoàn trả", null, null },
                    { new Guid("5d6c1a10-0000-4c3a-9a10-000000000003"), "OtherIncome", true, new DateTimeOffset(new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, null, "Receipt", true, true, true, "Thu khác", null, null },
                    { new Guid("5d6c1a10-0000-4c3a-9a10-000000000004"), "TransferWithdrawal", false, new DateTimeOffset(new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, null, "Receipt", true, true, true, "Chuyển/Rút", null, null },
                    { new Guid("5d6c1a10-0000-4c3a-9a10-000000000005"), "Goods", false, new DateTimeOffset(new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, null, "Payment", true, true, false, "Chi tiền trả NCC", null, null },
                    { new Guid("5d6c1a10-0000-4c3a-9a10-000000000006"), "Payroll", true, new DateTimeOffset(new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, null, "Payment", true, true, false, "Chi trả lương NV", null, null },
                    { new Guid("5d6c1a10-0000-4c3a-9a10-000000000007"), "OtherExpense", true, new DateTimeOffset(new DateTime(2026, 11, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 7, 0, 0, 0)), null, null, "Payment", true, true, true, "Chi phí khác", null, null }
                });

            // The receiving bank account of the cooperative (the one the VietQR codes pay into) is the first bank account of the cash book.
            migrationBuilder.Sql(
                @"INSERT INTO CashAccounts (Id, Kind, BankBin, ProviderName, AccountNumber, HolderName, IsActive, CreatedAt)
                  SELECT NEWID(), 'Bank', BankAccount_BankBin, BankAccount_BankName, BankAccount_AccountNumber, BankAccount_AccountHolderName, 1, SYSDATETIMEOFFSET()
                  FROM Cooperatives
                  WHERE BankAccount_BankBin IS NOT NULL AND BankAccount_AccountNumber IS NOT NULL AND BankAccount_AccountHolderName IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPayments_CashAccountId",
                table: "SupplierPayments",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CashAccounts_Kind_ProviderName_AccountNumber",
                table: "CashAccounts",
                columns: new[] { "Kind", "ProviderName", "AccountNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashVouchers_CashAccountId",
                table: "CashVouchers",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CashVouchers_Code",
                table: "CashVouchers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashVouchers_CollectedByAccountId",
                table: "CashVouchers",
                column: "CollectedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CashVouchers_Fund_VoucherDate",
                table: "CashVouchers",
                columns: new[] { "Fund", "VoucherDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CashVouchers_TypeId",
                table: "CashVouchers",
                column: "TypeId");

            migrationBuilder.CreateIndex(
                name: "IX_CashVoucherTypes_Direction_IsActive",
                table: "CashVoucherTypes",
                columns: new[] { "Direction", "IsActive" });

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierPayments_CashAccounts_CashAccountId",
                table: "SupplierPayments",
                column: "CashAccountId",
                principalTable: "CashAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SupplierPayments_CashAccounts_CashAccountId",
                table: "SupplierPayments");

            migrationBuilder.DropTable(
                name: "CashParties");

            migrationBuilder.DropTable(
                name: "CashVouchers");

            migrationBuilder.DropTable(
                name: "CashAccounts");

            migrationBuilder.DropTable(
                name: "CashVoucherTypes");

            migrationBuilder.DropIndex(
                name: "IX_SupplierPayments_CashAccountId",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "CashAccountId",
                table: "SupplierPayments");
        }
    }
}
