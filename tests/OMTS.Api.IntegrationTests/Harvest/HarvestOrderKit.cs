using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// A harvest lot is recorded for a purchase request (decision of 11/11/2026). Tests about the lot itself harvest for one Accepted request per host, written straight to the
/// database: it sends no notice and needs no trader, work plan or staff. Tests about the request make their own.
/// </summary>
public static class HarvestOrderKit
{
    private static readonly ConditionalWeakTable<IServiceProvider, Lazy<Task<Guid>>> DefaultOrders = new();

    /// <summary>The Accepted request the lots of this client's host harvest for when a test names none.</summary>
    public static Task<Guid> DefaultOrderAsync(HttpClient client) => DefaultOrderAsync(TestHosts.ServicesOf(client));

    public static Task<Guid> HarvestOrderAsync(this AuthApiFactory factory) => DefaultOrderAsync(factory.Services);

    /// <summary>The cages of the default order: the lots of a request never add up to more than it accepted (13/11/2026), and the tests of a host record many, up to the 100,000 cage maximum.</summary>
    public const int DefaultAcceptedCages = 1_000_000;

    private static Task<Guid> DefaultOrderAsync(IServiceProvider services) =>
        DefaultOrders.GetValue(services, s => new Lazy<Task<Guid>>(() => OrderAsync(s, acceptedCages: DefaultAcceptedCages))).Value;

    /// <summary>A request of a new customer profile and product, written in <paramref name="statusId"/>; Accepted for 1,000 cages unless said otherwise.</summary>
    public static async Task<Guid> OrderAsync(
        IServiceProvider services, int statusId = StatusIds.PurchaseRequest.Accepted, bool harvestDone = false, int acceptedCages = 1000)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(7)).DateTime);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new Customer { CooperativeId = await db.Cooperatives.Select(c => c.Id).FirstAsync(), Name = $"Thương lái thu hoạch {suffix}" };
        var product = new Product { Name = $"Hàu thu hoạch {suffix}", UnitPrice = 30_000m };
        var accepted = statusId == StatusIds.PurchaseRequest.Accepted;
        var request = new PurchaseRequest
        {
            Code = $"YC-TEST-{suffix}",
            CustomerId = customer.Id,
            Customer = customer,
            ProductId = product.Id,
            Product = product,
            ProductName = product.Name,
            RequestedCages = acceptedCages,
            PickupDate = today.AddDays(2),
            ClientOperationId = Guid.NewGuid(),
            SentAt = now,
            StatusId = statusId,
            AcceptedCages = accepted ? acceptedCages : null,
            AgreedPickupDate = accepted ? today.AddDays(2) : null,
            PayLaterAgreed = accepted ? false : null,
            ResponseNote = statusId == StatusIds.PurchaseRequest.Declined ? "Hết hàu" : null,
            RespondedAt = accepted || statusId == StatusIds.PurchaseRequest.Declined ? now : null,
        };
        if (harvestDone)
        {
            request.HarvestCompletedAt = now;
            request.HarvestCompletedByAccountId = await db.Accounts.Select(a => a.Id).FirstAsync();
        }

        db.AddRange(customer, product, request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    public static Task<HttpResponseMessage> PostHarvestDoneAsync(this HttpClient owner, Guid requestId, string? rowVersion) =>
        owner.PostAsJsonAsync($"/api/purchase-requests/{requestId}/harvest-done", new { rowVersion });

    public static Task<HttpResponseMessage> PostClearHarvestDoneAsync(this HttpClient owner, Guid requestId, string? rowVersion) =>
        owner.PostAsJsonAsync($"/api/purchase-requests/{requestId}/harvest-done/clear", new { rowVersion });

    /// <summary>The staff detail of the request, read again for its current version.</summary>
    public static Task<JsonElement> RequestDetailAsync(this HttpClient staff, Guid requestId) =>
        staff.GetFromJsonAsync<JsonElement>($"/api/purchase-requests/{requestId}");

    /// <summary>Marks the harvest done with the current version and answers the detail.</summary>
    public static async Task<JsonElement> MarkHarvestDoneAsync(this HttpClient owner, Guid requestId)
    {
        var current = await owner.RequestDetailAsync(requestId);
        return await (await owner.PostHarvestDoneAsync(requestId, current.GetProperty("rowVersion").GetString())).ReadAsync(HttpStatusCode.OK);
    }
}
