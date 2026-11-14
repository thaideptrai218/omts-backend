using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>CLAIM-03 on a real SQL Server: of two parallel saves that touch the same invoice one wins and nothing is applied twice; the filtered unique index is the last line of defence.</summary>
public class SalesReturnSqlServerTests
{
    private const int Rounds = 3;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, Guid RaftId, Guid ProductId, Guid CustomerId);

    private static async Task<Setup> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var product = await owner.CreateProductAsync(unitPrice: ListPrice);
        var customer = await owner.CreateCustomerAsync();
        return new Setup(host, owner, raftId, product.Id(), customer.Id());
    }

    private static async Task<JsonElement> SoldAsync(Setup setup)
    {
        var lotId = (await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1))).Id();
        var sold = await setup.Owner.SellAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lotId, 10m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), lotId));
        return await setup.Owner.InvoiceAsync(sold.GetProperty("invoice").Id());
    }

    private static Task<int> ReturnsAsync(Setup setup, Guid invoiceId) => setup.Host.QueryAsync(db => db.SalesReturns.CountAsync(r => r.InvoiceId == invoiceId));

    [SqlServerFact]
    public async Task Two_returns_of_one_invoice_at_once_record_one()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var debtBefore = await setup.Owner.CustomerDebtAsync(setup.CustomerId);
            var invoice = await SoldAsync(setup);

            var answers = await Task.WhenAll(
                setup.Owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale")), setup.Owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale")));

            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
            var loser = answers.Single(a => a.StatusCode != HttpStatusCode.Created);
            Assert.Contains(await loser.CodeOrStatusAsync(), new[] { "409 MSG-GEN-09", "409 MSG06" });
            Assert.Equal(1, await ReturnsAsync(setup, invoice.Id()));
            Assert.Equal(debtBefore, await setup.Owner.CustomerDebtAsync(setup.CustomerId));
        }
    }

    [SqlServerFact]
    public async Task A_return_and_a_correction_of_one_version_never_both_apply()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var invoice = await SoldAsync(setup);

            var answers = await Task.WhenAll(
                setup.Owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale")), setup.Owner.CorrectAsync(invoice.Id(), PriceCut(invoice, 100_000m)));

            var returned = answers[0].StatusCode == HttpStatusCode.Created;
            var corrected = answers[1].StatusCode == HttpStatusCode.OK;
            Assert.True(returned ^ corrected, $"return {answers[0].StatusCode}, correction {answers[1].StatusCode}");
            Assert.Equal(returned ? 1 : 0, await ReturnsAsync(setup, invoice.Id()));
            var after = await setup.Owner.InvoiceAsync(invoice.Id());
            Assert.Equal(returned ? 0m : 100_000m, after.Money("priceCutTotal"));
        }
    }

    [SqlServerFact]
    public async Task A_return_and_a_cancellation_of_one_version_never_both_apply()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var invoice = await SoldAsync(setup);

            var answers = await Task.WhenAll(
                setup.Owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale")), setup.Owner.CancelInvoiceAsync(invoice.Id(), Cancellation(invoice)));

            var returned = answers[0].StatusCode == HttpStatusCode.Created;
            var cancelled = answers[1].StatusCode == HttpStatusCode.OK;
            Assert.True(returned ^ cancelled, $"return {answers[0].StatusCode}, cancellation {answers[1].StatusCode}");
            Assert.Equal(returned ? 1 : 0, await ReturnsAsync(setup, invoice.Id()));
            var status = await setup.Host.QueryAsync(db => db.SalesInvoices.Where(i => i.Id == invoice.Id()).Select(i => i.StatusId).SingleAsync());
            Assert.Equal(returned ? StatusIds.SalesInvoice.Completed : StatusIds.SalesInvoice.Cancelled, status);
        }
    }

    [SqlServerFact]
    public async Task A_return_and_a_payment_of_the_customer_at_once_do_not_lose_either()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var accountant = await setup.Host.StaffAsync(null, StaffRole.Accountant);
        for (var round = 0; round < Rounds; round++)
        {
            var invoice = await SoldAsync(setup);
            var debtBefore = await setup.Owner.CustomerDebtAsync(setup.CustomerId);

            var answers = await Task.WhenAll(
                setup.Owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale")),
                accountant.RecordAsync(CustomerPaymentTestKit.MoneyBody(setup.CustomerId, 300_000m)));

            var returned = answers[0].StatusCode == HttpStatusCode.Created;
            var paid = answers[1].StatusCode == HttpStatusCode.Created;
            Assert.True(returned || paid, $"return {answers[0].StatusCode}, payment {answers[1].StatusCode}");
            Assert.Contains(answers, a => a.StatusCode == HttpStatusCode.Conflict || (returned && paid));
            var expected = debtBefore - (returned ? 300_000m : 0m) - (paid ? 300_000m : 0m);
            Assert.Equal(expected, await setup.Owner.CustomerDebtAsync(setup.CustomerId));
        }
    }

    [SqlServerFact]
    public async Task The_unique_index_refuses_a_second_return_that_stands_and_allows_one_after_a_cancelled_one()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var invoice = await SoldAsync(setup);
        var saved = await setup.Owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));

        // A second standing row, bypassing the service, is refused by the filtered unique index.
        var duplicate = () => setup.Host.QueryAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO SalesReturns (Id, Code, InvoiceId, ReturnDate, Reason, Value, RecordedByAccountId, StatusId, CreatedAt) SELECT NEWID(), N'TL-DEMO', InvoiceId, ReturnDate, Reason, Value, RecordedByAccountId, {StatusIds.SalesReturn.Recorded}, CreatedAt FROM SalesReturns WHERE InvoiceId = {invoice.Id()}"));
        await Assert.ThrowsAnyAsync<Exception>(duplicate);

        await setup.Owner.CancelSalesReturnAsync(saved.GetProperty("return").Id(), CancelBody(saved.GetProperty("return")));
        await duplicate();
        Assert.Equal(2, await ReturnsAsync(setup, invoice.Id()));
    }

    [SqlServerFact]
    public async Task A_thrown_away_return_has_the_discarded_kg_on_its_line()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var invoice = await SoldAsync(setup);
        var lotId = invoice.GetProperty("lines")[0].GetProperty("harvestLotId").GetGuid();

        await setup.Owner.ReturnedAsync(
            invoice.Id(), ReturnBody(invoice, "ThrownAway").With("thrownAwayLots", new[] { new { harvestLotId = lotId, stillForSale = false, leftoverKg = 0m } }));

        var line = await setup.Host.QueryAsync(db => db.Set<SalesReturnLine>().SingleAsync(l => l.HarvestLotId == lotId));
        Assert.Equal((SalesReturnOutcome.ThrownAway, 10m, 10m), (line.Outcome, line.ReturnedKg, line.DiscardedKg));
    }
}
