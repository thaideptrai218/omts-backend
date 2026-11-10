using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-11 and SALE-04 helpers: products and profiles made through the API, and linked profiles as registration leaves them.</summary>
public static class SalesCatalogueTestKit
{
    public static async Task<JsonElement> CreateProductAsync(this HttpClient owner, string? name = null, decimal unitPrice = 60_000)
    {
        var response = await owner.PostAsJsonAsync("/api/products", new { name = name ?? $"Oyster {NewCode()}", unitPrice });
        return (await response.ReadAsync(HttpStatusCode.Created)).GetProperty("product");
    }

    public static async Task<JsonElement> CreateCustomerAsync(this HttpClient client, string? name = null, string? phone = null)
    {
        var response = await client.PostAsJsonAsync("/api/customers", new { name = name ?? $"Buyer {NewCode()}", phoneNumber = phone ?? NewPhone() });
        return (await response.ReadAsync(HttpStatusCode.Created)).GetProperty("customer");
    }

    public static string RowVersion(this JsonElement element) => element.GetProperty("rowVersion").GetString()!;

    public static string? Text(this JsonElement element, string property) => element.GetProperty(property).GetString();

    /// <summary>The current row version of a profile, whoever saved it.</summary>
    public static async Task<JsonElement> CustomerAsync(this HttpClient client, Guid id) =>
        await (await client.GetAsync($"/api/customers/{id}")).ReadAsync(HttpStatusCode.OK);

    /// <summary>Merges with the current row versions of both profiles.</summary>
    public static async Task<HttpResponseMessage> MergeAsync(this HttpClient client, Guid keptId, Guid mergedId, bool ownerApproved = false)
    {
        var kept = await client.CustomerAsync(keptId);
        var merged = await client.CustomerAsync(mergedId);
        return await client.PostMergeAsync(keptId, mergedId, kept.RowVersion(), merged.RowVersion(), ownerApproved);
    }

    public static Task<HttpResponseMessage> PostMergeAsync(this HttpClient client, Guid keptId, Guid? mergedId, string? keptRowVersion, string? mergedRowVersion, bool ownerApproved = false) =>
        client.PostAsJsonAsync($"/api/customers/{keptId}/merge", new { mergedCustomerId = mergedId, keptRowVersion, mergedRowVersion, ownerApproved });

    /// <summary>Every answer of a race is one of <paramref name="allowed"/> (status and code), so never a 500.</summary>
    public static async Task AssertAnswersAsync(IEnumerable<HttpResponseMessage> responses, params string[] allowed)
    {
        foreach (var response in responses)
        {
            var answer = await response.CodeOrStatusAsync();
            Assert.True(allowed.Contains(answer), $"Unexpected answer {answer}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    /// <summary>A trader account with its profile, as registration makes them; the phone number is the one of the account unless given.</summary>
    public static async Task<(Account Trader, Customer Profile)> LinkedProfileAsync(this AuthApiFactory factory, string? name = null, Action<Customer>? configure = null)
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var profile = new Customer { Code = OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit.NewCustomerCode(), CooperativeId = cooperative.Id, AccountId = trader.Id, Name = name ?? $"Trader {NewCode()}", PhoneNumber = trader.PhoneNumber };
        configure?.Invoke(profile);
        await factory.UseDbAsync(db => db.Customers.Add(profile));
        return (trader, profile);
    }
}
