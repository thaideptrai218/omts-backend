using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Queries;
using OMTS.Infrastructure.Persistence.Repositories;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Independent QA of SALE-07 and SALE-08 on SQL Server: races with a payment and with the Accountant, the longest reason, values too large, the statement query, and the migration over saved invoices.</summary>
public class SalesInvoiceChangeQaTestsSqlServer
{
    private const int Rounds = 4;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId, Guid RaftId, Guid ProductId, Guid CustomerId);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<Setup> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5]));
        var product = await owner.CreateProductAsync(unitPrice: ListPrice);
        var customer = await owner.CreateCustomerAsync();
        return new Setup(host, owner, zoneId, raftId, product.Id(), customer.Id());
    }

    private static async Task<Guid> LotAsync(Setup setup) => (await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1))).Id();

    private static async Task<JsonElement> SoldAsync(Setup setup, Guid lotId, decimal kg = 10m, decimal cash = 0m) =>
        (await setup.Owner.SellAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lotId, kg, ListPrice)], Cash(cash, "PayLater", payLaterOwnerAgreed: true), lotId))).GetProperty("invoice");

    private static async Task<(Actor Accountant, Guid UserId)> AccountantAsync(Setup setup)
    {
        using var scope = setup.Host.Api.Services.CreateScope();
        var staff = new Account
        {
            Username = AcsTestKit.NewCode(), DisplayName = "Accountant", SubjectType = SubjectType.Staff, StatusId = StatusIds.Account.Active,
            PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(AcsTestKit.Password),
            PhoneNumber = AcsTestKit.NewPhone(), PhoneVerifiedAt = DateTimeOffset.UtcNow,
        };
        await setup.Host.UseDbAsync(db => db.Accounts.Add(staff));
        var grant = await setup.Host.Api.GrantAsync(staff, StaffRole.Accountant, null, setup.Host.Owner.Account.Id);
        return (new Actor(await setup.Host.LoginAsync(staff.Username), staff, grant), staff.Id);
    }

    // BR-DEBT-02 (correction versus payment): whichever way the race goes, the debt follows what was saved and nothing answers 500.
    [SqlServerFact]
    public async Task A_price_cut_and_a_payment_of_the_same_customer_never_answer_500_and_the_debt_follows_what_was_saved()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var (accountant, _) = await AccountantAsync(setup);
        for (var round = 0; round < Rounds; round++)
        {
            var invoice = await SoldAsync(setup, await LotAsync(setup));

            var answers = await Task.WhenAll(
                setup.Owner.CorrectAsync(invoice.Id(), PriceCut(invoice, 50_000m)),
                accountant.Client.RecordAsync(CustomerPaymentTestKit.MoneyBody(setup.CustomerId, 50_000m)));

            Assert.All(answers, a => Assert.True(a.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict, $"{(int)a.StatusCode}"));
            var cut = (await setup.Owner.InvoiceAsync(invoice.Id())).Money("priceCutTotal");
            var paymentSaved = answers[1].StatusCode == HttpStatusCode.Created;
            Assert.Equal(cut == 50_000m, answers[0].StatusCode == HttpStatusCode.OK);
            Assert.True(cut is 0m or 50_000m);
            var debt = await setup.Owner.CustomerDebtAsync(setup.CustomerId);
            var figures = await setup.Host.QueryAsync(async db =>
                (Invoices: await db.SalesInvoices.Where(i => i.CustomerId == setup.CustomerId && i.StatusId != StatusIds.SalesInvoice.Cancelled).SumAsync(i => i.Total),
                 Paid: await db.CustomerPayments.Where(p => p.CustomerId == setup.CustomerId && p.StatusId != StatusIds.CustomerPayment.Cancelled).SumAsync(p => p.Amount)));
            Assert.Equal(figures.Invoices - figures.Paid, debt);
            Assert.True(paymentSaved || answers[1].StatusCode == HttpStatusCode.Conflict);
        }
    }

    // BR-SALE-11 versus DEBT-02.2: cash given back and a parallel confirmation cannot both win, and the invoice and the payment agree.
    [SqlServerFact]
    public async Task Cash_given_back_and_a_parallel_confirmation_leave_the_invoice_and_the_payment_agreeing()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var (accountant, _) = await AccountantAsync(setup);
        for (var round = 0; round < Rounds; round++)
        {
            var invoice = await SoldAsync(setup, await LotAsync(setup), cash: 100_000m);
            var payment = await setup.Host.QueryAsync(db => db.CustomerPayments.Where(p => p.InvoiceId == invoice.Id()).Select(p => p.Id).SingleAsync());

            var answers = await Task.WhenAll(
                setup.Owner.CancelInvoiceAsync(invoice.Id(), Cancellation(invoice).With("cashGivenBack", GivenBack(payment, true))),
                accountant.Client.ConfirmAsync([payment]));

            Assert.All(answers, a => Assert.NotEqual(HttpStatusCode.InternalServerError, a.StatusCode));
            var saved = await setup.Owner.InvoiceAsync(invoice.Id());
            var status = await setup.Host.QueryAsync(db => db.CustomerPayments.Where(p => p.Id == payment).Select(p => p.StatusId).SingleAsync());
            if (saved.Text("status") == "Cancelled")
            {
                Assert.Equal(StatusIds.CustomerPayment.Cancelled, status);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, answers[1].StatusCode);
                Assert.Equal(StatusIds.CustomerPayment.Confirmed, status);
            }
        }
    }

    // Two parallel data entry corrections that insert and delete lines: the lines are one whole version, never a mix, and no 500 from the unique line number.
    [SqlServerFact]
    public async Task Parallel_corrections_of_the_lines_keep_one_whole_version()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var lotA = await LotAsync(setup);
            var lotB = await LotAsync(setup);
            var lotC = await LotAsync(setup);
            var invoice = (await setup.Owner.SellAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lotA, 1m, ListPrice), Line(setup.ProductId, lotB, 1m, ListPrice)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), lotA, lotB))).GetProperty("invoice");

            var answers = await Task.WhenAll(
                setup.Owner.CorrectAsync(invoice.Id(), DataEntry(invoice, [Line(setup.ProductId, lotA, 2m, ListPrice), Line(setup.ProductId, lotB, 2m, ListPrice), Line(setup.ProductId, lotC, 2m, ListPrice)], lotA, lotB, lotC)),
                setup.Owner.CorrectAsync(invoice.Id(), DataEntry(invoice, [Line(setup.ProductId, lotA, 3m, ListPrice)], lotA)));

            Assert.All(answers, a => Assert.True(a.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)a.StatusCode}"));
            var saved = await setup.Owner.InvoiceAsync(invoice.Id());
            var kg = saved.GetProperty("lines").EnumerateArray().Select(l => l.Money("kg")).ToList();
            Assert.True(kg.SequenceEqual([2m, 2m, 2m]) || kg.SequenceEqual([3m]) || kg.SequenceEqual([1m, 1m]), string.Join(",", kg));
            var numbers = saved.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("lineNumber").GetInt32()).ToList();
            Assert.Equal(numbers.Count, numbers.Distinct().Count());
        }
    }

    // The longest reason the rules accept fits every column it is written to, in Vietnamese too.
    [SqlServerFact]
    public async Task The_longest_reason_fits_every_column_it_reaches()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var reason = string.Concat(Enumerable.Repeat("Lý do ", 167)).Trim()[..1000];
        var cashSale = await SoldAsync(setup, await LotAsync(setup), cash: 100_000m);
        var cut = (await setup.Owner.CorrectedAsync(cashSale.Id(), PriceCut(cashSale, 1_000m).With("reason", reason))).GetProperty("invoice");
        var payment = await setup.Host.QueryAsync(db => db.CustomerPayments.Where(p => p.InvoiceId == cashSale.Id()).Select(p => p.Id).SingleAsync());

        var cancelled = await setup.Owner.CancelledAsync(cashSale.Id(), Cancellation(cut).With("reason", reason).With("cashGivenBack", GivenBack(payment, true)));

        Assert.Equal(reason, cancelled.GetProperty("invoice").Text("cancelReason"));
        Assert.Equal(reason, await setup.Host.QueryAsync(db => db.CustomerPayments.Where(p => p.Id == payment).Select(p => p.CancelReason).SingleAsync()));
        Assert.Equal(reason, cut.GetProperty("lastCorrectionReason").GetString());
    }

    // Values that fit a field alone can overflow a column together: SALE-07.0.E2 answers MSG-GEN-01, not a 500.
    [SqlServerFact]
    public async Task Other_charges_that_overflow_the_total_answer_msg_gen_01()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var lot = await LotAsync(setup);
        var invoice = await SoldAsync(setup, lot);

        var answer = await setup.Owner.CorrectAsync(invoice.Id(), DataEntry(invoice, [Line(setup.ProductId, lot, 10m, ListPrice)], lot).With("otherCharges", 999_999_999_999_999_999m));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("MSG-GEN-01", (await answer.ReadAsync(HttpStatusCode.BadRequest)).Text("code"));
    }

    // N+1: the statement reads the corrections of all its invoices with one command, whatever their number.
    [SqlServerFact]
    public async Task The_statement_of_many_corrected_invoices_costs_the_same_commands_as_of_two()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var lot = await LotAsync(setup);
        var few = (await setup.Owner.CreateCustomerAsync()).Id();
        var many = (await setup.Owner.CreateCustomerAsync()).Id();

        async Task CorrectedInvoicesAsync(Guid customer, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var sold = (await setup.Owner.SellAsync(Invoice(customer, [Line(setup.ProductId, lot, 1m, ListPrice)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), lot))).GetProperty("invoice");
                await setup.Owner.CorrectedAsync(sold.Id(), PriceCut(sold, 1_000m));
            }
        }

        await CorrectedInvoicesAsync(few, 2);
        await CorrectedInvoicesAsync(many, 12);

        async Task<int> CommandsAsync(Guid customer)
        {
            var counter = new CommandCounter();
            await using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>().UseSqlServer(setup.Host.Database.ConnectionString).AddInterceptors(counter).Options);
            var query = new CustomerDebtQuery(db);
            var service = new CustomerDebtService(query, new CustomerDebtReader(query, TimeProvider.System), new SalesInvoiceCorrectionLog(new SalesInvoiceRepository(db)), TimeProvider.System);
            var statement = await service.StatementAsync(customer, Today.AddDays(-1), Today, CancellationToken.None);
            Assert.True(statement.IsSuccess);
            return counter.Count;
        }

        Assert.Equal(await CommandsAsync(few), await CommandsAsync(many));
    }

    // Additive and reversible over saved invoices: a saved invoice reads the same after Down and Up, and the new fields are empty on the old rows.
    [SqlServerFact]
    public async Task Migration_down_and_up_keeps_saved_invoices_readable()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var lot = await LotAsync(setup);
        var invoice = await SoldAsync(setup, lot, cash: 100_000m);

        await using (var migratorDb = setup.Host.Database.CreateContext())
        {
            var migrator = migratorDb.GetService<IMigrator>();
            await migrator.MigrateAsync("20261108020405_CustomerPayments");
            await migrator.MigrateAsync();
        }

        var read = await setup.Owner.InvoiceAsync(invoice.Id());
        Assert.Equal((invoice.Text("code"), invoice.Money("total")), (read.Text("code"), read.Money("total")));
        Assert.Equal(JsonValueKind.Null, read.GetProperty("cancelReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("lastCorrectionKind").ValueKind);
        Assert.Empty(read.GetProperty("corrections").EnumerateArray());
        await setup.Owner.CorrectedAsync(invoice.Id(), PriceCut(read, 1_000m));
    }
}
