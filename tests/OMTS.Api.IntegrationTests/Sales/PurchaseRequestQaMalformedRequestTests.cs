using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: no body and no query answers 500, whatever a client sends.</summary>
public class PurchaseRequestQaMalformedRequestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, Guid ProductId)> WorldAsync()
    {
        var owner = await factory.PurchasingOwnerAsync();
        return (owner, await factory.TraderAsync(), (await owner.CreateProductAsync()).Id());
    }

    public static TheoryData<string> Junk() => new()
    {
        "",
        "{",
        "null",
        "[]",
        "123",
        "\"x\"",
        """{ "requestedKg": "abc", "acceptedKg": "abc", "note": 1 }""",
        """{ "requestedKg": 1e400, "acceptedKg": 1e400, "note": 1 }""",
        """{ "requestedKg": true, "acceptedKg": true, "note": 1 }""",
        """{ "pickupDate": "31/12/2099", "note": 1 }""",
        """{ "pickupDate": "2099-02-30", "note": 1 }""",
        """{ "pickupDate": 20991231, "note": 1 }""",
        """{ "productId": "not-a-guid", "note": 1 }""",
        """{ "note": 123, "reason": 123 }""",
        """{ "note": "\ud83d", "reason": "\ud83d" }""",
        """{ "payLaterAgreed": "yes", "note": 1 }""",
        """{ "rowVersion": 12 }""",
        new string('[', 2000),
    };

    // Whatever cannot be read as JSON or as the type of a field is a 400 on every route, never a 500.
    [Theory]
    [MemberData(nameof(Junk))]
    public async Task Junk_is_a_400_or_a_refusal_on_every_route_and_never_a_500(string body)
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);

        foreach (var response in new[]
        {
            await trader.Client.PostAsJsonRawAsync("/api/me/purchase-requests", body),
            await trader.Client.PostAsJsonRawAsync($"/api/me/purchase-requests/{request.Id()}/cancel", body),
            await owner.PostAsJsonRawAsync($"/api/purchase-requests/{request.Id()}/accept", body),
            await owner.PostAsJsonRawAsync($"/api/purchase-requests/{request.Id()}/decline", body),
        })
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode} for {body}: {text}");
            Assert.Equal("MSG-GEN-01", JsonDocument.Parse(text).RootElement.Text("code"));
        }

        Assert.Equal(1, (await (await trader.Client.GetAsync("/api/me/purchases")).ReadAsync(HttpStatusCode.OK)).GetProperty("total").GetInt32());
    }

    // The client list, §1: "text that cannot be read as the field's type". Pinned, an observation: only a crafted client sends it, and the field then
    // reads "request", not the field at fault. InvalidModelStateResponse is not of this PR.
    [Fact]
    public async Task An_unreadable_field_answers_msg_gen_01_on_the_field_request()
    {
        var (_, trader, productId) = await WorldAsync();

        var field = await (await trader.Client.PostAsJsonRawAsync("/api/me/purchase-requests", $$"""{ "productId": "{{productId}}", "requestedKg": "abc", "pickupDate": "2099-01-01" }""")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        Assert.Equal("request", field);
    }

    // Only the four fields of the form are read: a request cannot be made for another profile, as answered, or with a status.
    [Fact]
    public async Task Fields_outside_the_form_are_ignored_when_sending_and_a_request_has_no_edit_route()
    {
        var (owner, trader, productId) = await WorldAsync();
        var other = await factory.TraderAsync();

        var request = (await (await trader.Client.PostAsJsonAsync("/api/me/purchase-requests", new
        {
            productId, requestedKg = 5m, pickupDate = PurchaseRequestTestKit.Today, customerId = other.Profile.Id, statusId = 3202, acceptedKg = 5m, payLaterAgreed = true, id = Guid.Empty,
        })).ReadAsync(HttpStatusCode.Created)).GetProperty("request");

        var view = await owner.ViewedByStaffAsync(request.Id());
        Assert.Equal((trader.Profile.Id, 3201, JsonValueKind.Null), (view.GetProperty("customer").GetProperty("id").GetGuid(), view.GetProperty("statusId").GetInt32(), view.GetProperty("response").ValueKind));
        Assert.NotEqual(Guid.Empty, request.Id());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await trader.Client.PutAsJsonAsyncRaw($"/api/me/purchase-requests/{request.Id()}", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await trader.Client.DeleteAsync($"/api/me/purchase-requests/{request.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.PutAsJsonAsyncRaw($"/api/purchase-requests/{request.Id()}", "{}")).StatusCode);
    }

    [Fact]
    public async Task Another_content_type_is_a_415_and_not_a_500()
    {
        var (_, trader, productId) = await WorldAsync();

        var response = await trader.Client.PostAsync("/api/me/purchase-requests", new StringContent($"productId={productId}", Encoding.UTF8, "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Theory]
    [InlineData("page=abc", HttpStatusCode.BadRequest)]
    [InlineData("pageSize=abc", HttpStatusCode.BadRequest)]
    [InlineData("page=99999999999", HttpStatusCode.BadRequest)]
    [InlineData("page=0&pageSize=0", HttpStatusCode.OK)]
    [InlineData("page=-5&pageSize=-5", HttpStatusCode.OK)]
    [InlineData("page=2147483647&pageSize=2147483647", HttpStatusCode.OK)]
    public async Task A_strange_page_is_clamped_or_a_400_on_both_lists(string query, HttpStatusCode expected)
    {
        var (owner, trader, _) = await WorldAsync();

        Assert.Equal(expected, (await trader.Client.GetAsync($"/api/me/purchases?{query}")).StatusCode);
        Assert.Equal(expected, (await owner.GetAsync($"/api/purchase-requests?{query}")).StatusCode);
    }

    [Theory]
    [InlineData("statusId=abc", HttpStatusCode.BadRequest)]
    [InlineData("customerId=abc", HttpStatusCode.BadRequest)]
    [InlineData("statusId=2147483647", HttpStatusCode.OK)]
    [InlineData("statusId=-1", HttpStatusCode.OK)]
    [InlineData("customerId=00000000-0000-0000-0000-000000000000", HttpStatusCode.OK)]
    public async Task A_strange_filter_is_a_400_or_an_empty_list(string query, HttpStatusCode expected)
    {
        var (owner, _, _) = await WorldAsync();

        Assert.Equal(expected, (await owner.GetAsync($"/api/purchase-requests?{query}")).StatusCode);
    }
}
