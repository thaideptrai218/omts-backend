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

/// <summary>DEBT-03 on SQL Server: the list is translated to SQL and sends the same few commands for one customer as for many (no query per customer).</summary>
public class CustomerDebtSqlServerTests
{
    [SqlServerFact]
    public async Task The_debt_list_sends_the_same_commands_for_one_customer_as_for_many_and_reads_the_same_debts_as_the_api()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var zone = host.NewZoneEntity("KV" + AcsTestKit.NewCode()[..6]);
        await host.UseDbAsync(db => db.FarmingZones.Add(zone));
        var today = TimeProvider.System.Today();
        var sequence = 0;

        async Task<Guid> OwingCustomerAsync(string name, decimal total, decimal paid, decimal paidBack = 0m)
        {
            var customer = new Customer { CooperativeId = host.CooperativeId, Name = name };
            var invoice = new SalesInvoice
            {
                Code = $"HD9{++sequence:D5}", InvoicedAt = DateTimeOffset.UtcNow.AddDays(-sequence), InvoiceDate = today.AddDays(-sequence), ZoneId = zone.Id,
                Customer = customer, Subtotal = total, Total = total, PaymentTerm = SalesPaymentTerm.PayLater, CreatedByAccountId = host.Owner.Account.Id,
            };
            await host.UseDbAsync(db =>
            {
                db.SalesInvoices.Add(invoice);
                db.CustomerPayments.Add(Payment(customer, CustomerPaymentKind.Payment, paid, invoice, StatusIds.CustomerPayment.AwaitingConfirmation));
                if (paidBack > 0)
                {
                    db.CustomerPayments.Add(Payment(customer, CustomerPaymentKind.PaidBack, paidBack, null, StatusIds.CustomerPayment.Confirmed));
                }
            });
            return customer.Id;

            CustomerPayment Payment(Customer c, CustomerPaymentKind kind, decimal amount, SalesInvoice? with, int statusId) => new()
            {
                Code = $"PT9{++sequence:D5}", Customer = c, Kind = kind, Amount = amount, PaymentDate = today, Method = CustomerPaymentMethod.Cash, Invoice = with, StatusId = statusId,
            };
        }

        var first = await OwingCustomerAsync("Plan customer 0", 1_000_000m, 400_000m);
        var (oneCommands, one) = await ListAsync(host);
        Assert.Equal(600_000m, Assert.Single(one).Debt);

        for (var i = 1; i <= 25; i++)
        {
            // Every fifth one paid more than its invoice and got part of it back: money owed back, no age.
            await OwingCustomerAsync($"Plan customer {i}", 500_000m, i % 5 == 0 ? 700_000m : 100_000m, i % 5 == 0 ? 100_000m : 0m);
        }

        var (manyCommands, many) = await ListAsync(host);

        Assert.Equal(oneCommands, manyCommands);
        Assert.InRange(oneCommands, 1, 6);
        Assert.Equal(26, many.Count);
        Assert.Equal((600_000m, 1), many.Where(r => r.Customer.Id == first).Select(r => (r.Debt, r.DebtAgeDays!.Value)).Single());
        Assert.Equal(5, many.Count(r => r.Debt == -100_000m && r.DebtAgeDays is null));
        Assert.All(many.Where(r => r.Debt > 0), r => Assert.NotNull(r.DebtAgeDays));

        // The API on the same database answers the same debts and totals.
        var list = await host.Owner.Client.DebtListAsync("?pageSize=100");
        Assert.Equal(many.Select(r => (r.Customer.Id, r.Debt)), list.Rows().Select(r => (r.GetProperty("customer").Id(), r.GetProperty("debt").GetDecimal())));
        Assert.Equal((600_000m + 20 * 400_000m, 500_000m), (list.GetProperty("receivablesTotal").GetDecimal(), list.GetProperty("owedBackTotal").GetDecimal()));
        Assert.Equal(26, list.GetProperty("awaitingConfirmationCount").GetInt32());
        Assert.Equal([first], (await host.Owner.Client.DebtListAsync("?search=customer%200")).Rows().Select(r => r.GetProperty("customer").Id()));
    }

    /// <summary>The list as the service builds it, on a context that counts every command it sends.</summary>
    private static async Task<(int Commands, IReadOnlyList<CustomerDebtRow> Rows)> ListAsync(FarmingSqlServerHost host)
    {
        var counter = new CommandCounter();
        await using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(host.Database.ConnectionString).AddInterceptors(counter).Options);
        var query = new CustomerDebtQuery(db);
        var service = new CustomerDebtService(query, new CustomerDebtReader(query, TimeProvider.System), TimeProvider.System);
        var list = await service.ListAsync(null, 1, 100, CancellationToken.None);
        return (counter.Count, list.Page.Items);
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

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
