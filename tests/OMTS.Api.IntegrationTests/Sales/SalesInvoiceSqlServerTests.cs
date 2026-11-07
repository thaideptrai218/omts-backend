using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// SALE-05 on SQL Server: real row versions and parallel saves. Every answer of a race is a 2xx or a 409 (or 404 for a lot already gone), never a 500. Run these alone as well as in a group.
/// </summary>
public class SalesInvoiceSqlServerTests
{
    private const int Rounds = 3;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, HttpClient Manager, Guid ZoneId, Guid RaftId, Guid ProductId, Guid CustomerId);

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
        var manager = await host.StaffInZoneAsync(zoneId);
        return new Setup(host, owner, manager.Client, zoneId, raftId, product.Id(), customer.Id());
    }

    private static async Task AssertAnsweredAsync(params HttpResponseMessage[] answers)
    {
        foreach (var answer in answers)
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.True(answer.IsSuccessStatusCode || answer.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound, $"{(int)answer.StatusCode}: {text}");
        }
    }

    // SALE-05.0.E2: two sales of the last kg of a lot, both "sold the whole lot": one closes it, the other is told the lot is Closed.
    [SqlServerFact]
    public async Task Two_invoices_on_the_last_kg_of_a_lot_save_one_and_the_other_answers_msg_gen_09()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(round % 5 + 1));
            var body = Invoice(setup.CustomerId, [Line(setup.ProductId, lot.Id(), 50m)], Cash(1_500_000m));

            var answers = await Task.WhenAll(setup.Manager.PostInvoiceAsync(body), setup.Owner.PostInvoiceAsync(body));

            await AssertAnsweredAsync(answers);
            var won = Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Created);
            var lost = Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Conflict);
            var refused = await lost.ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal(("MSG-GEN-09", $"Harvest lot {lot.Text("lotCode")} is Closed and cannot be used for this action."), (refused.Text("code"), refused.Text("detail")));
            var invoiceId = (await won.ReadAsync(HttpStatusCode.Created)).GetProperty("invoice").Id();
            var (invoices, payments, closedBy) = await setup.Host.QueryAsync(async db => (
                await db.SalesInvoiceLines.CountAsync(l => l.HarvestLotId == lot.Id()),
                await db.CustomerPayments.CountAsync(p => p.InvoiceId == invoiceId),
                await db.HarvestLots.Where(l => l.Id == lot.Id()).Select(l => l.ClosedBySalesInvoiceId).SingleAsync()));
            Assert.Equal((1, 1, (Guid?)invoiceId), (invoices, payments, closedBy));
            Assert.Equal(1, await setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Closed" && e.RecordId == lot.Id())));
        }
    }

    // Two sales of one lot that leave it for sale: the loser of the row version runs again on fresh reads and is saved too.
    [SqlServerFact]
    public async Task Parallel_invoices_that_keep_a_lot_for_sale_are_both_saved_with_their_own_codes()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var lot = await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1));

        var answers = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            (i % 2 == 0 ? setup.Owner : setup.Manager).PostInvoiceAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lot.Id(), 10m)], Cash(300_000m), lot.Id()))));

        await AssertAnsweredAsync(answers);
        var saved = new List<JsonElement>();
        foreach (var answer in answers)
        {
            saved.Add((await answer.ReadAsync(HttpStatusCode.Created)).GetProperty("invoice"));
        }

        Assert.Equal(4, saved.Select(i => i.Text("code")).Distinct().Count());
        Assert.Equal(40m, (await setup.Owner.GetHarvestLotAsync(lot.Id())).GetProperty("figures").Money("invoicedKg"));
        Assert.Equal(StatusIds.HarvestLot.Open, await setup.Host.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lot.Id()).Select(l => l.StatusId).SingleAsync()));
        Assert.Equal(0m, await setup.Owner.CustomerDebtAsync(setup.CustomerId));
    }

    // A sale and a correction of the lot at the same moment: both write the lot row, so they never both build on a stale one.
    [SqlServerFact]
    public async Task An_invoice_and_a_correction_of_its_lot_in_parallel_never_answer_500_and_keep_the_sale_whole()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(round % 5 + 1));
            var lanes = lot.Numbers("laneNumbers").Select(n => n.ToString()).ToList();
            var correction = new HarvestCorrection(new HarvestFields { Lanes = lanes, Cages = "9" }, "Wrong count", lot.Text("rowVersion"));

            var answers = await Task.WhenAll(
                setup.Manager.PostInvoiceAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lot.Id(), 20m)], Cash(600_000m))),
                setup.Owner.PutHarvestLotAsync(lot.Id(), correction));

            await AssertAnsweredAsync(answers);
            Assert.Equal(HttpStatusCode.Created, answers[0].StatusCode);
            var stored = await setup.Host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lot.Id()));
            Assert.Equal(StatusIds.HarvestLot.Closed, stored.StatusId);
            Assert.Equal(answers[1].IsSuccessStatusCode ? 9 : 8, stored.CageCount);
            Assert.Equal(answers[1].IsSuccessStatusCode ? 1 : 0, await setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Corrected" && e.RecordId == lot.Id())));
        }
    }

    // BR-HARVEST-05: a removal and a sale at the same moment never leave an invoice on a removed lot.
    [SqlServerFact]
    public async Task An_invoice_and_the_removal_of_its_lot_in_parallel_leave_either_the_sale_or_the_removal()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var lot = await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(round % 5 + 1));

            var answers = await Task.WhenAll(
                setup.Manager.PostInvoiceAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lot.Id(), 20m)], Cash(600_000m), lot.Id())),
                setup.Owner.PostRemoveLotAsync(lot.Id(), "Recorded by mistake", lot.Text("rowVersion")));

            // The sale that runs again after the removal finds no lot: MSG-GEN-01 on the line, as for a lot that never existed.
            Assert.True(answers[0].StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest or HttpStatusCode.Conflict, $"{(int)answers[0].StatusCode}: {await answers[0].Content.ReadAsStringAsync()}");
            await AssertAnsweredAsync(answers[1]);
            var (lots, lines) =await setup.Host.QueryAsync(async db => (await db.HarvestLots.CountAsync(l => l.Id == lot.Id()), await db.SalesInvoiceLines.CountAsync(l => l.HarvestLotId == lot.Id())));
            Assert.True(answers[0].IsSuccessStatusCode != answers[1].IsSuccessStatusCode, $"{answers[0].StatusCode} and {answers[1].StatusCode}");
            Assert.Equal(answers[0].IsSuccessStatusCode ? (1, 1) : (0, 0), (lots, lines));
        }
    }

    // The order of the commands of one save is EF's: a sale whose lot was removed after it was read may reach its line before the lot. The missing row is a conflict (read again), not a 500.
    [SqlServerFact]
    public async Task A_save_that_points_to_a_row_removed_meanwhile_is_a_conflict_and_saves_nothing()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        using var scope = setup.Host.Api.Services.CreateScope();
        var invoices = scope.ServiceProvider.GetRequiredService<ISalesInvoiceRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        invoices.Add(new SalesInvoice
        {
            Code = "HD999999", InvoicedAt = DateTimeOffset.UtcNow, InvoiceDate = Today, ZoneId = setup.ZoneId, Subtotal = 30_000m, Total = 30_000m, CreatedByAccountId = setup.Host.Owner.Account.Id,
            Lines = [new SalesInvoiceLine { LineNumber = 1, ProductId = setup.ProductId, HarvestLotId = Guid.NewGuid(), Kg = 1m, UnitPrice = 30_000m, ListPrice = 30_000m, Amount = 30_000m }],
        });

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => unitOfWork.SaveChangesAsync());

        Assert.Equal(0, await setup.Host.QueryAsync(db => db.SalesInvoices.CountAsync()));
    }
    [SqlServerFact]
    public async Task Parallel_saves_with_one_deduplication_key_create_one_invoice_and_answer_it_to_each()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var lot = await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1));
        var body = Invoice(setup.CustomerId, [Line(setup.ProductId, lot.Id(), 10m)], Cash(300_000m), lot.Id()).With("deduplicationKey", $"key-{Guid.NewGuid():N}");

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => setup.Manager.PostInvoiceAsync(body)));

        var ids = new List<Guid>();
        foreach (var answer in answers)
        {
            ids.Add((await answer.ReadAsync(HttpStatusCode.Created)).GetProperty("invoice").Id());
        }

        Assert.Single(ids.Distinct());
        Assert.Equal((1, 1), (await setup.Host.QueryAsync(db => db.SalesInvoices.CountAsync()), await setup.Host.QueryAsync(db => db.CustomerPayments.CountAsync())));
        Assert.Equal(1, await setup.Host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "SALE_COMPLETED")));
    }
}
