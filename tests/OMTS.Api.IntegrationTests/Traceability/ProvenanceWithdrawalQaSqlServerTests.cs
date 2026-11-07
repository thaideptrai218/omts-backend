using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Traceability.Tracing;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// QA of TRACE-04 (OMTS-222) on SQL Server: a sale saved while the withdrawal reads its traders (BR-TRACE-07), and the race fix of the end of the sale (6b / T1a): parallel ends of
/// the sales of one Published lot must all finish, as they did before the fix.
/// </summary>
public class ProvenanceWithdrawalQaSqlServerTests
{
    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, HttpClient Manager, Guid RaftId, Guid ProductId, Guid CustomerId, Guid OtherCustomerId);

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
        var customer = await owner.CreateCustomerAsync(name: "DEMO QA trader one");
        var other = await owner.CreateCustomerAsync(name: "DEMO QA trader two");
        var manager = await host.StaffInZoneAsync(zoneId);
        return new Setup(host, owner, manager.Client, raftId, product.Id(), customer.Id(), other.Id());
    }

    /// <summary>A sale of 1 kg of the lot; <paramref name="soldWholeLot"/> ticks "sold the whole lot", which closes the lot (and so writes its row).</summary>
    private static async Task<JsonElement> SellAsync(Setup setup, Guid lotId, Guid customerId, bool soldWholeLot = false) =>
        (await setup.Manager.SellAsync(Invoice(customerId, [Line(setup.ProductId, lotId, 1m)], Cash(ListPrice), soldWholeLot ? [] : [lotId]))).GetProperty("invoice");

    private static async Task<Guid> PublishedLotAsync(Setup setup)
    {
        var lotId = (await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1))).Id();
        await SellAsync(setup, lotId, setup.CustomerId);
        await (await setup.Owner.PostPublishAsync(lotId)).ReadAsync(HttpStatusCode.OK);
        return lotId;
    }

    // BR-TRACE-07 under a race: the Zone Manager saves a sale of the Published lot (lot kept open, the usual case when one lot goes to several traders) after the withdrawal has read
    // the invoices and before it saves. SALE-05 marks its lots changed (SalesInvoiceFactsReader), so the withdrawal loses its save, reads again and tells both traders, once.
    [SqlServerFact]
    public async Task A_sale_saved_while_the_withdrawal_reads_the_invoices_has_its_trader_notified()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var lotId = await PublishedLotAsync(setup);
        var hook = new SaleLinesHook();
        await using var hooked = Hooked(setup.Host, hook);
        var owner = await LoginAsync(hooked, setup.Host.Owner.Account.Username);
        hook.Once = () => SellAsync(setup, lotId, setup.OtherCustomerId);

        var answer = await (await owner.PostWithdrawAsync(lotId)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(HarvestLotOriginState.Withdrawn, (await LotAsync(setup, lotId)).OriginState);
        Assert.Equal(["DEMO QA trader one", "DEMO QA trader two"], answer.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("customerName").GetString()).Order());
        Assert.Equal(1, await setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == lotId && e.Action == ProvenanceWithdrawalTestKit.WithdrawnAction)));
        Assert.Equal(2, await setup.Host.QueryAsync(db => db.Notifications.CountAsync(n =>
            n.RelatedRecordId == lotId && n.Type == NotificationTypes.ProvenanceWithdrawn && n.Channel != NotificationChannel.InApp)));
    }

    // 6b finish semantics after the race fix (ProvenancePublisher marks every Published lot of the invoice changed): several Zone Managers end the sales of one Published lot at the
    // port at the same moment. Before T3 these saves shared no row; now they all write the lot row, and the end of the sale has 3 attempts, so with 4 parallel ends one loses three
    // times and answers 409 MSG06 (deterministic here: 1 of 4, 3 of 6). Each must still be 200 with its QR code.
    [SqlServerFact]
    public async Task Parallel_ends_of_the_sales_of_one_published_lot_all_finish_with_a_qr_code()
    {
        const int Sales = 4;
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < 2; round++)
        {
            var lotId = await PublishedLotAsync(setup);
            var invoices = new List<Guid>();
            for (var i = 0; i < Sales; i++)
            {
                invoices.Add((await SellAsync(setup, lotId, i % 2 == 0 ? setup.CustomerId : setup.OtherCustomerId)).Id());
            }

            var answers = await Task.WhenAll(invoices.Select(id => setup.Manager.FinishAsync(id, true)));

            var statuses = new List<string>();
            foreach (var a in answers)
            {
                statuses.Add(a.StatusCode == HttpStatusCode.OK ? "200" : $"{(int)a.StatusCode} {await a.Content.ReadAsStringAsync()}");
            }

            Assert.All(statuses, s => Assert.Equal("200", s));
            var withQr = await setup.Host.QueryAsync(db => db.SalesInvoices.AsNoTracking().CountAsync(i => invoices.Contains(i.Id) && i.QrToken != null));
            Assert.Equal(Sales, withQr);
        }
    }

    private static Task<HarvestLot> LotAsync(Setup setup, Guid lotId) => setup.Host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));

    /// <summary>The same API and database, with the forward trace query wrapped so that a test can act between the read of the invoices and the save of the withdrawal.</summary>
    private static WebApplicationFactory<Program> Hooked(FarmingSqlServerHost host, SaleLinesHook hook) => host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
    {
        var original = services.Last(d => d.ServiceType == typeof(ILotTraceQuery));
        services.Remove(original);
        services.AddScoped<ILotTraceQuery>(sp => new HookedTraceQuery((ILotTraceQuery)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), hook));
    }));

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> api, string username)
    {
        var client = api.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private sealed class SaleLinesHook
    {
        public Func<Task>? Once { get; set; }

        public int Reads => _reads;

        public Func<Task>? Take()
        {
            Interlocked.Increment(ref _reads);
            return Interlocked.Exchange(ref _taken, 1) == 0 ? Once : null;
        }

        private int _taken;
        private int _reads;
    }

    private sealed class HookedTraceQuery(ILotTraceQuery inner, SaleLinesHook hook) : ILotTraceQuery
    {
        public Task<IReadOnlyList<HarvestLot>> SearchAsync(string? lotCode, string? invoiceCode, PermittedZones zones, int max, CancellationToken cancellationToken) =>
            inner.SearchAsync(lotCode, invoiceCode, zones, max, cancellationToken);

        public async Task<IReadOnlyList<LotSaleLine>> SaleLinesAsync(Guid lotId, CancellationToken cancellationToken)
        {
            var lines = await inner.SaleLinesAsync(lotId, cancellationToken);
            if (hook.Take() is { } action)
            {
                await action();
            }

            return lines;
        }

        public Task<IReadOnlyList<TraceCorrectionRow>> CorrectionsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken) =>
            inner.CorrectionsAsync(recordIds, cancellationToken);
    }
}
