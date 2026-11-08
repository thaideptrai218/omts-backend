using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: what SALE-01 and SALE-03 read (BR-SALE-01, SALE-01.0.E1), and that no body or query answers 500.</summary>
public class PurchaseRequestQaValidationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, Guid ProductId)> WorldAsync()
    {
        var owner = await factory.PurchasingOwnerAsync();
        return (owner, await factory.TraderAsync(), (await owner.CreateProductAsync()).Id());
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("0.001", true)]
    [InlineData("1", true)]
    [InlineData("1.500", true)]
    [InlineData("1.5000", true)]
    [InlineData("1e2", true)]
    [InlineData("999999999999999.999", true)]
    [InlineData("0", false)]
    [InlineData("-0.001", false)]
    [InlineData("0.0001", false)]
    [InlineData("0.0005", false)]
    [InlineData("1000000000000000", false)]
    [InlineData("null", false)]
    public async Task The_requested_cages_is_above_0_with_at_most_3_decimals_and_fits_its_column(string kg, bool valid)
    {
        var (_, trader, productId) = await WorldAsync();
        var body = $$"""{ "productId": "{{productId}}", "requestedCages": {{kg}}, "pickupDate": "{{Date(PurchaseRequestTestKit.Today)}}" }""";

        var response = await trader.Client.PostAsJsonRawAsync("/api/me/purchase-requests", body);

        if (valid)
        {
            await response.ReadAsync(HttpStatusCode.Created);
        }
        else
        {
            Assert.Equal(["Requested cages"], await response.InvalidFieldsAsync());
        }
    }

    [Fact]
    public async Task A_date_before_today_is_refused_today_and_a_far_date_are_taken_and_a_missing_one_is_refused()
    {
        var (owner, trader, productId) = await WorldAsync();

        Assert.Equal(["Pickup date"], await (await trader.Client.PostRequestAsync(productId, pickupDate: PurchaseRequestTestKit.Today.AddDays(-1))).InvalidFieldsAsync());
        var missing = await trader.Client.PostAsJsonAsync("/api/me/purchase-requests", new { productId, requestedCages = 1m });
        Assert.Equal(["Pickup date"], await missing.InvalidFieldsAsync());
        await (await trader.Client.PostRequestAsync(productId, pickupDate: PurchaseRequestTestKit.Today)).ReadAsync(HttpStatusCode.Created);

        // No limit in the SRS: pinned, and a question for the PM.
        var far = await trader.Client.PlaceAsync(productId, pickupDate: DateOnly.MaxValue);
        await (await owner.AcceptAsync(far, pickupDate: DateOnly.MaxValue)).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_field_wrong_at_once_is_pointed_out_together()
    {
        var (_, trader, _) = await WorldAsync();

        var fields = await (await trader.Client.PostAsJsonAsync("/api/me/purchase-requests", new { productId = Guid.NewGuid(), requestedCages = 0m, pickupDate = "2000-01-01", note = new string('a', 1001) })).InvalidFieldsAsync();

        Assert.Equal(["Product", "Requested cages", "Pickup date", "Note"], fields);
    }

    // SALE-01.0.E2: MSG-GEN-09, the entries are kept; another wrong field comes first so the form points at it.
    [Fact]
    public async Task A_discontinued_product_answers_msg_gen_09_saves_nothing_and_a_resumed_one_works_again()
    {
        var (owner, trader, productId) = await WorldAsync();
        (await owner.PostAsync($"/api/products/{productId}/discontinue", null)).EnsureSuccessStatusCode();
        var name = (await (await owner.GetAsync($"/api/products/{productId}")).ReadAsync(HttpStatusCode.OK)).Text("name");

        var refused = await (await trader.Client.PostRequestAsync(productId)).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-09");
        Assert.Equal(($"Product {name} is Discontinued and cannot be used for this action.", "Product"), (refused.GetProperty("detail").GetString(), refused.Text("field")));
        Assert.Equal(["Requested cages"], await (await trader.Client.PostRequestAsync(productId, cages: 0)).InvalidFieldsAsync());
        Assert.Equal(0, (await (await trader.Client.GetAsync("/api/me/purchase-requests")).ReadAsync(HttpStatusCode.OK)).GetProperty("total").GetInt32());

        (await owner.PostAsync($"/api/products/{productId}/resume", null)).EnsureSuccessStatusCode();
        await (await trader.Client.PostRequestAsync(productId)).ReadAsync(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("  ")]
    [InlineData("​‍﻿")]
    [InlineData("­")]
    public async Task A_note_or_a_reason_of_nothing_visible_counts_as_none_and_a_decline_needs_a_real_note(string blank)
    {
        var (owner, trader, productId) = await WorldAsync();

        var request = await trader.Client.PlaceAsync(productId, note: blank);
        Assert.Equal(JsonValueKind.Null, request.GetProperty("note").ValueKind);
        Assert.Equal(["Note"], await (await owner.DeclineAsync(request, blank)).InvalidFieldsAsync());
        var accepted = await trader.Client.PlaceAsync(productId);
        var answer = await (await owner.AcceptAsync(accepted, note: blank)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("response").GetProperty("note").ValueKind);
        var cancelled = await (await trader.Client.CancelAsync(request, blank)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("cancelReason").ValueKind);
    }

    [Fact]
    public async Task Each_text_takes_1000_characters_after_trimming_and_refuses_1001()
    {
        var (owner, trader, productId) = await WorldAsync();
        var full = new string('ữ', 1000);
        var tooLong = full + "a";

        var placed = await trader.Client.PlaceAsync(productId, note: "  " + full + "  ");
        Assert.Equal(full, placed.Text("note"));
        Assert.Equal(["Note"], await (await trader.Client.PostRequestAsync(productId, note: tooLong)).InvalidFieldsAsync());
        Assert.Equal(["Note"], await (await owner.AcceptAsync(placed, note: tooLong)).InvalidFieldsAsync());
        Assert.Equal(["Note"], await (await owner.DeclineAsync(placed, tooLong)).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await trader.Client.CancelAsync(placed, tooLong)).InvalidFieldsAsync());
        (await owner.DeclineAsync(placed, full)).EnsureSuccessStatusCode();
        var other = await trader.Client.PlaceAsync(productId);
        (await trader.Client.CancelAsync(other, full)).EnsureSuccessStatusCode();
    }
}
