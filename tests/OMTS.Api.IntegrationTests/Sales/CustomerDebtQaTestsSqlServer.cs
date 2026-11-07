using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Queries;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA of DEBT-03 on SQL Server (PR 8a): one customer with many invoices and payments (own cash, pooled transfers, cancelled rows, invoices of the same time) has one
/// debt in the list, the statement, its unpaid invoices and the customer view; the statement sends the same commands for 1 invoice as for 150.
/// </summary>
public class CustomerDebtQaTestsSqlServer
{
    private const int ManyInvoices = 150;

    [SqlServerFact]
    public async Task A_customer_with_many_invoices_and_payments_has_one_debt_everywhere_and_the_statement_does_not_query_per_row()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var zone = host.NewZoneEntity("KV" + AcsTestKit.NewCode()[..6]);
        await host.UseDbAsync(db => db.FarmingZones.Add(zone));
        var today = TimeProvider.System.Today();
        var sequence = 0;
        var oldest = today.AddDays(-ManyInvoices / 3);

        async Task<(Guid Id, decimal Debt, int Rows)> CustomerAsync(int invoiceCount)
        {
            var customer = new Customer { CooperativeId = host.CooperativeId, Name = "QA many " + AcsTestKit.NewCode() };
            var invoices = new List<SalesInvoice>();
            var payments = new List<CustomerPayment>();
            for (var i = 0; i < invoiceCount; i++)
            {
                var date = oldest.AddDays(i / 3);
                var total = (i % 7 + 1) * 100_000m;
                // Every two invoices of a day share their time: the code decides between them.
                var invoice = new SalesInvoice
                {
                    Code = $"HD8{++sequence:D5}", InvoicedAt = new DateTimeOffset(date.ToDateTime(new TimeOnly(9 + i % 3 / 2, 0)), TimeSpan.FromHours(7)), InvoiceDate = date,
                    ZoneId = zone.Id, Customer = customer, Subtotal = total, Total = total, PaymentTerm = SalesPaymentTerm.PayLater, CreatedByAccountId = host.Owner.Account.Id,
                    StatusId = i % 11 == 0 ? StatusIds.SalesInvoice.Cancelled : StatusIds.SalesInvoice.Completed,
                };
                invoices.Add(invoice);
                if (i % 4 == 0)
                {
                    payments.Add(Payment(customer, total, invoice, date, StatusIds.CustomerPayment.AwaitingConfirmation));
                }
                else if (i % 4 == 1)
                {
                    payments.Add(Payment(customer, total / 2, invoice, date, StatusIds.CustomerPayment.Confirmed));
                }

                if (i % 5 == 0)
                {
                    payments.Add(Payment(customer, 150_000m, null, date, StatusIds.CustomerPayment.Confirmed));
                }

                if (i % 9 == 0)
                {
                    payments.Add(Payment(customer, 999_000m, null, date, StatusIds.CustomerPayment.Cancelled));
                }
            }

            await host.UseDbAsync(db =>
            {
                db.SalesInvoices.AddRange(invoices);
                db.CustomerPayments.AddRange(payments);
            });
            var debt = invoices.Where(i => i.StatusId != StatusIds.SalesInvoice.Cancelled).Sum(i => i.Total)
                - payments.Where(p => p.StatusId != StatusIds.CustomerPayment.Cancelled).Sum(p => p.Amount);
            return (customer.Id, debt, invoices.Count + payments.Count);
        }

        CustomerPayment Payment(Customer c, decimal amount, SalesInvoice? with, DateOnly date, int statusId) => new()
        {
            Code = $"PT8{++sequence:D5}", Customer = c, Kind = CustomerPaymentKind.Payment, Amount = amount, PaymentDate = date,
            Method = with is null ? CustomerPaymentMethod.OtherTransfer : CustomerPaymentMethod.Cash, Invoice = with, StatusId = statusId,
        };

        var single = await CustomerAsync(2);
        var many = await CustomerAsync(ManyInvoices);
        Assert.True(many.Debt > 0, $"the vector should leave a debt, not {many.Debt}");

        var (singleCommands, _) = await StatementAsync(host, single.Id, oldest, today);
        var (manyCommands, statement) = await StatementAsync(host, many.Id, oldest, today);

        Assert.Equal(singleCommands, manyCommands);
        Assert.Equal((0m, many.Debt, many.Debt), (statement.OpeningDebt, statement.ClosingDebt, statement.DebtToday));
        Assert.Equal(many.Rows, statement.Entries.Count);
        Assert.Equal(many.Debt, statement.UnpaidInvoices.Sum(i => i.Unpaid));
        Assert.Equal(statement.UnpaidInvoices.OrderBy(i => i.InvoiceDate).ThenBy(i => i.Code, StringComparer.Ordinal).Select(i => i.Code), statement.UnpaidInvoices.Select(i => i.Code));
        Assert.Equal(today.DayNumber - statement.UnpaidInvoices[0].InvoiceDate.DayNumber, statement.DebtAgeDays);

        var list = await host.Owner.Client.DebtListAsync("?pageSize=100");
        Assert.Equal(many.Debt, list.RowOf(many.Id).GetProperty("debt").GetDecimal());
        Assert.Equal(statement.DebtAgeDays, list.RowOf(many.Id).GetProperty("debtAgeDays").GetInt32());
        Assert.Equal(many.Debt, await host.Owner.Client.CustomerDebtAsync(many.Id));
        var api = await host.Owner.Client.StatementAsync(many.Id, oldest, today);
        Assert.Equal((many.Debt, many.Rows), (api.GetProperty("debtToday").GetDecimal(), api.GetProperty("entries").GetArrayLength()));
    }

    /// <summary>The statement as the service builds it, on a context that counts every command it sends.</summary>
    private static async Task<(int Commands, CustomerDebtStatement Statement)> StatementAsync(FarmingSqlServerHost host, Guid customerId, DateOnly from, DateOnly to)
    {
        var counter = new CommandCounter();
        await using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(host.Database.ConnectionString).AddInterceptors(counter).Options);
        var query = new CustomerDebtQuery(db);
        var service = new CustomerDebtService(query, new CustomerDebtReader(query, TimeProvider.System), TimeProvider.System);
        var statement = await service.StatementAsync(customerId, from, to, CancellationToken.None);
        Assert.True(statement.IsSuccess);
        return (counter.Count, statement.Value!);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
