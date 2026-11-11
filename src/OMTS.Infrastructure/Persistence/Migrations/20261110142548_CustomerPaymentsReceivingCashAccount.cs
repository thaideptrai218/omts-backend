using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OMTS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerPaymentsReceivingCashAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The transfers made with the VietQR code of an invoice paid into the cooperative's receiving bank account: name the cash account that is that account, so the cash book
            // shows them under it. A cooperative whose cash book holds no such account leaves them as they are.
            migrationBuilder.Sql(@"
UPDATE p SET p.[CashAccountId] = a.[Id]
FROM [CustomerPayments] p
CROSS JOIN [Cooperatives] c
INNER JOIN [CashAccounts] a ON a.[Kind] = N'Bank' AND a.[BankBin] = c.[BankAccount_BankBin] AND UPPER(a.[AccountNumber]) = UPPER(c.[BankAccount_AccountNumber])
WHERE p.[Method] = N'VietQrInvoice' AND p.[CashAccountId] IS NULL AND c.[BankAccount_AccountNumber] IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
