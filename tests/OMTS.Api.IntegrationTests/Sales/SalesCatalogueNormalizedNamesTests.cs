using System.Net;
using System.Net.Http.Json;
using System.Text;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Names as a person reads them, on a real SQL Server: what is stored, what an edit compares, what a search finds.</summary>
public class SalesCatalogueNormalizedNamesTests
{
    // The stored value is the normalized one, so the unique index holds it and a read shows it.
    [SqlServerFact]
    public async Task The_normalized_name_is_what_is_stored()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var typed = "  Hàu ​ sữa\t".Normalize(NormalizationForm.FormD);

        var product = await owner.CreateProductAsync(typed);
        var customer = await owner.CreateCustomerAsync(name: typed);

        Assert.Equal("Hàu sữa", (await (await owner.GetAsync($"/api/products/{product.Id()}")).ReadAsync(HttpStatusCode.OK)).Text("name"));
        Assert.Equal("Hàu sữa", (await owner.CustomerAsync(customer.Id())).Text("name"));
    }

    // SALE-11.1: an edit compares the same way, and an edit that only changes the spelling changes nothing.
    [SqlServerFact]
    public async Task An_edit_to_another_spelling_of_a_name_is_refused_and_of_its_own_writes_no_change()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var taken = await owner.CreateProductAsync("Ốc hương đỏ");
        var product = await owner.CreateProductAsync("Hàu sữa", 60_000);

        var other = await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = "Ốc  hương đỏ".Normalize(NormalizationForm.FormD), unitPrice = 60_000, rowVersion = product.RowVersion() });
        Assert.Equal(taken.Id(), (await other.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02")).GetProperty("existingId").GetGuid());

        var own = await owner.PutAsJsonAsync($"/api/products/{product.Id()}", new { name = "Hàu  sữa".Normalize(NormalizationForm.FormD), unitPrice = 60_000, rowVersion = product.RowVersion() });
        Assert.Equal(product.RowVersion(), (await own.ReadAsync(HttpStatusCode.OK)).GetProperty("product").RowVersion());
    }

    // A search is read the same way as a name, on both lists.
    [SqlServerFact]
    public async Task A_search_in_another_spelling_finds_the_name()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var product = await owner.CreateProductAsync("Ốc hương đỏ");
        var customer = await owner.CreateCustomerAsync(name: "Chị Lương Thư");

        var products = await (await owner.GetAsync($"/api/products?search={Uri.EscapeDataString("hương đỏ".Normalize(NormalizationForm.FormD))}")).ReadAsync(HttpStatusCode.OK);
        var customers = await (await owner.GetAsync($"/api/customers?search={Uri.EscapeDataString("Lương  Thư".Normalize(NormalizationForm.FormD))}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(product.Id(), Assert.Single(products.EnumerateArray()).Id());
        Assert.Equal(customer.Id(), Assert.Single(customers.GetProperty("items").EnumerateArray()).Id());
    }

    // A search longer than any name finds nothing; the longest name is still found; a part of a stored number too.
    [SqlServerFact]
    public async Task The_search_bound_is_the_longest_name_and_a_part_of_a_number_is_still_found()
    {
        await using var host = await FarmingHost.StartAsync();
        var owner = host.Owner.Client;
        var (name, phone) = (new string('x', 200), NewPhone());
        var customer = await owner.CreateCustomerAsync(name: name, phone: phone);
        await owner.CreateProductAsync(name);

        var longest = await (await owner.GetAsync($"/api/customers?search={name}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(customer.Id(), Assert.Single(longest.GetProperty("items").EnumerateArray()).Id());
        Assert.Single((await (await owner.GetAsync($"/api/products?search={name}")).ReadAsync(HttpStatusCode.OK)).EnumerateArray());

        var tooLong = await (await owner.GetAsync($"/api/customers?search={name}x")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0, tooLong.GetProperty("total").GetInt32());
        Assert.Empty((await (await owner.GetAsync($"/api/products?search={name}x")).ReadAsync(HttpStatusCode.OK)).EnumerateArray());

        var part = await (await owner.GetAsync($"/api/customers?search={phone[..7]}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(customer.Id(), Assert.Single(part.GetProperty("items").EnumerateArray()).Id());
    }
}
