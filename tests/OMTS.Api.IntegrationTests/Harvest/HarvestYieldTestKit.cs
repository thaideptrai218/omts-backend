using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Harvest;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using SalesCatalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>A seeded raft of the Owner with a product at 30,000 VND/kg and a customer profile, ready to sell its lots.</summary>
public sealed record YieldSetup(SeededRaft Raft, HttpClient Owner, Guid ProductId, Guid CustomerId);

/// <summary>
/// What CLAIM-03 will add to the sales port, set by a test over the real invoices: the real <see cref="IHarvestLotSalesQuery"/> answers the invoiced kg of the real sales invoices, and
/// the returned, back-for-sale and discarded kg of a lot come from <see cref="Returns"/>. It feeds the seam where the sales returns will come in, nothing else.
/// </summary>
public sealed class ReturnsSeamApiFactory : AuthApiFactory
{
    public Dictionary<Guid, LotSalesFigures> Returns { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IHarvestLotSalesQuery));
            services.Remove(real);
            services.AddScoped<IHarvestLotSalesQuery>(sp => new WithReturns((IHarvestLotSalesQuery)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!), Returns));
        });
    }

    private sealed class WithReturns(IHarvestLotSalesQuery real, Dictionary<Guid, LotSalesFigures> returns) : IHarvestLotSalesQuery
    {
        public async Task<IReadOnlyDictionary<Guid, LotSalesFigures>> FiguresAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
            (await real.FiguresAsync(lotIds, cancellationToken)).ToDictionary(
                f => f.Key,
                f => returns.TryGetValue(f.Key, out var r) ? f.Value with { ReturnedKg = r.ReturnedKg, BackForSaleKg = r.BackForSaleKg, DiscardedKg = r.DiscardedKg } : f.Value);

        public Task<bool> IsOnAnyInvoiceAsync(Guid lotId, CancellationToken cancellationToken) => real.IsOnAnyInvoiceAsync(lotId, cancellationToken);

        public Task<IReadOnlySet<Guid>> LotsOnAnyInvoiceAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
            real.LotsOnAnyInvoiceAsync(lotIds, cancellationToken);
    }
}

/// <summary>HARVEST-04 calls and readers of the report.</summary>
public static class HarvestYieldTestKit
{
    public const string Yield = "/api/harvest-yield";

    /// <summary>The query string of the filters; null leaves a filter out.</summary>
    public static string YieldFilters(DateOnly? from = null, DateOnly? to = null, string? groupBy = null, params Guid[] zoneIds)
    {
        var parts = new List<string>();
        if (from is { } f)
        {
            parts.Add($"from={f:yyyy-MM-dd}");
        }

        if (to is { } t)
        {
            parts.Add($"to={t:yyyy-MM-dd}");
        }

        if (groupBy is not null)
        {
            parts.Add($"groupBy={groupBy}");
        }

        parts.AddRange(zoneIds.Select(z => $"zoneIds={z}"));
        return parts.Count == 0 ? string.Empty : $"?{string.Join('&', parts)}";
    }

    public static Task<HttpResponseMessage> GetYieldAsync(this HttpClient client, string filters = "") => client.GetAsync($"{Yield}{filters}");

    public static Task<HttpResponseMessage> GetYieldExportAsync(this HttpClient client, string filters = "") => client.GetAsync($"{Yield}/export{filters}");

    /// <summary>A report that must be answered.</summary>
    public static async Task<JsonElement> YieldAsync(this HttpClient client, string filters = "") => await (await client.GetYieldAsync(filters)).ReadAsync(HttpStatusCode.OK);

    public static List<JsonElement> Groups(this JsonElement report) => report.GetProperty("groups").EnumerateArray().ToList();

    public static JsonElement Group(this JsonElement report, Guid key) => report.Groups().Single(g => g.GetProperty("key").GetGuid() == key);

    /// <summary>The lot wherever it is in the groups.</summary>
    public static JsonElement YieldLot(this JsonElement report, Guid lotId) =>
        report.Groups().SelectMany(g => g.GetProperty("lots").EnumerateArray()).Single(l => l.Id() == lotId);

    public static List<Guid> YieldLotIds(this JsonElement report) => report.Groups().SelectMany(g => g.GetProperty("lots").EnumerateArray()).Select(l => l.Id()).ToList();

    /// <summary>The figures of a group, a lot or the totals: estimated, sold, lost, difference and the Not final mark.</summary>
    public static (decimal Estimated, decimal Sold, decimal Lost, decimal Difference, bool NotFinal) Figures(this JsonElement element) =>
        (element.Kg("estimatedKg"), element.Kg("soldKg"), element.Kg("lostKg"), element.Kg("differenceKg"), element.GetProperty("notFinal").GetBoolean());

    /// <summary>Moves the harvest date of a lot: the period filter reads it.</summary>
    public static Task DateLotAsync(this AuthApiFactory api, Guid lotId, DateOnly date) => api.UpdateAsync<HarvestLot>(lotId, l => l.HarvestDate = date);

    public static async Task<YieldSetup> YieldSetupAsync(this AuthApiFactory api)
    {
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var product = await SalesCatalogue.CreateProductAsync(owner, unitPrice: ListPrice);
        var customer = await SalesCatalogue.CreateCustomerAsync(owner);
        return new YieldSetup(raft, owner, product.Id(), customer.Id());
    }

    /// <summary>A cash sale of <paramref name="kg"/> of the lot at the list price; the lot is closed unless <paramref name="soldWholeLot"/> is false.</summary>
    public static async Task<JsonElement> SellKgAsync(this HttpClient seller, YieldSetup setup, Guid lotId, decimal kg, bool soldWholeLot = true) =>
        await seller.SellAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lotId, kg)], Cash(kg * ListPrice), soldWholeLot ? [] : [lotId]));
}
