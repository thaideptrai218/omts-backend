using System.Net;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223 on a real SQL Server: paging and search input as the database sees them.</summary>
public class SalesCatalogueQaSearchSqlServerTests
{
    // Never a 500 from malformed input: 2147483646 x 100 overflows the offset of the page.
    [SqlServerFact]
    public async Task L1_a_very_large_page_number_is_empty_not_a_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        await owner.CreateCustomerAsync();

        var response = await owner.GetAsync("/api/customers?page=2147483647&pageSize=100");

        var body = await response.ReadAsync(HttpStatusCode.OK);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [SqlServerFact]
    public async Task A_large_page_that_fits_the_offset_is_empty()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        await owner.CreateCustomerAsync();

        var body = await (await owner.GetAsync("/api/customers?page=21474836&pageSize=100")).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    // The characters of LIKE are plain text, and odd search input is read as text, on both lists.
    [SqlServerFact]
    public async Task Search_treats_like_wildcards_as_text_and_never_answers_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        await owner.CreateCustomerAsync(name: "Chị Hoa");
        await owner.CreateProductAsync("Hàu sữa");

        foreach (var text in new[] { "%", "_", "[a-z]", "'", "\"", "--", "x y" })
        {
            var customers = await (await owner.GetAsync($"/api/customers?search={Uri.EscapeDataString(text)}")).ReadAsync(HttpStatusCode.OK);
            Assert.True(customers.GetProperty("total").GetInt32() == 0, $"customers matched [{text}]");
            var products = await (await owner.GetAsync($"/api/products?search={Uri.EscapeDataString(text)}")).ReadAsync(HttpStatusCode.OK);
            Assert.True(products.GetArrayLength() == 0, $"products matched [{text}]");
        }
    }

    // Never a 500 from malformed input: a search as long as a pasted page still only finds nothing.
    [SqlServerFact]
    public async Task L1_a_very_long_search_finds_nothing_and_is_not_a_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        await owner.CreateCustomerAsync(name: "Chị Hoa");
        await owner.CreateProductAsync("Hàu sữa");
        var answers = new List<string>();

        foreach (var length in new[] { 201, 2000, 3999, 4000, 4001, 6000 })
        {
            var text = new string('x', length);
            answers.Add($"{length}: customers {(int)(await owner.GetAsync($"/api/customers?search={text}")).StatusCode}, products {(int)(await owner.GetAsync($"/api/products?search={text}")).StatusCode}");
        }

        var error = host.Api.Logs.Lines.FirstOrDefault(l => l.Contains("Unhandled exception"));
        var cause = error?.Split('\n').FirstOrDefault(l => l.Contains("Exception"));
        Assert.True(answers.All(a => !a.Contains("500")), $"{string.Join("; ", answers)} | {cause}");
    }

    // Never a 500 from malformed input. A NUL may match nothing or everything, it only has to answer.
    [SqlServerFact]
    public async Task A_nul_in_the_search_is_not_a_500()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        await owner.CreateCustomerAsync(name: "Chị Hoa");
        await owner.CreateProductAsync("Hàu sữa");

        var customers = await owner.GetAsync("/api/customers?search=%00");
        var products = await owner.GetAsync("/api/products?search=%00");

        var error = host.Api.Logs.Lines.FirstOrDefault(l => l.Contains("Unhandled exception"));
        Assert.True((customers.StatusCode, products.StatusCode) == (HttpStatusCode.OK, HttpStatusCode.OK), $"customers {(int)customers.StatusCode}, products {(int)products.StatusCode}: {error}");
    }

    // SALE-04 step 2: the search ignores case on the server collation, diacritics not: pinned for the PM (unresolved question).
    [SqlServerFact]
    public async Task Pinned_the_search_ignores_case_but_not_diacritics()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var customer = await owner.CreateCustomerAsync(name: "Chị Hòa Cái Rồng");

        var byCase = await (await owner.GetAsync($"/api/customers?search={Uri.EscapeDataString("HÒA")}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(customer.Id(), Assert.Single(byCase.GetProperty("items").EnumerateArray()).Id());
        var plain = await (await owner.GetAsync("/api/customers?search=Hoa")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0, plain.GetProperty("total").GetInt32());
    }
}
