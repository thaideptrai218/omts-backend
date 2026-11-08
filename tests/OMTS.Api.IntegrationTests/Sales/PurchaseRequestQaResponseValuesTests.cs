using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: what an answer holds: the accepted kg, the agreement to pay later, the note (BR-SALE-02, SALE-03.0.E2).</summary>
public class PurchaseRequestQaResponseValuesTests(PurchaseRequestStubsFactory factory) : IClassFixture<PurchaseRequestStubsFactory>
{
    private async Task<(HttpClient Owner, TraderActor Trader, Guid ProductId)> WorldAsync()
    {
        var owner = await factory.PurchasingOwnerAsync();
        return (owner, await factory.TraderAsync(), (await owner.CreateProductAsync()).Id());
    }

    // BR-SALE-02: the accepted kg is the requested kg or a smaller kg, above 0.
    [Theory]
    [InlineData("100", true)]
    [InlineData("100.000", true)]
    [InlineData("99.999", true)]
    [InlineData("0.001", true)]
    [InlineData("100.001", false)]
    [InlineData("101", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("1.2345", false)]
    [InlineData("0.0004", false)]
    [InlineData(null, false)]
    public async Task The_accepted_cages_is_above_0_at_most_3_decimals_and_not_above_the_requested_cages(string? kg, bool valid)
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId, 100);
        var body = $$"""{ "acceptedCages": {{kg ?? "null"}}, "pickupDate": "{{PurchaseRequestTestKit.Today:yyyy-MM-dd}}", "payLaterAgreed": false, "rowVersion": {{JsonSerializer.Serialize(request.RowVersion())}} }""";

        var response = await owner.PostAsJsonRawAsync($"/api/purchase-requests/{request.Id()}/accept", body);

        if (valid)
        {
            Assert.Equal(decimal.Parse(kg!, System.Globalization.CultureInfo.InvariantCulture), (await response.ReadAsync(HttpStatusCode.OK)).GetProperty("response").GetProperty("acceptedCages").GetDecimal());
        }
        else
        {
            Assert.Equal(["Accepted cages"], await response.InvalidFieldsAsync());
            Assert.Equal(StatusIds.PurchaseRequest.AwaitingResponse, (await trader.Client.MineAsync(request.Id())).GetProperty("statusId").GetInt32());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_pay_later_flag_is_kept_as_chosen_and_stays_after_a_cancel(bool payLater)
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);

        var answer = await (await owner.AcceptAsync(request, payLater: payLater)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(payLater, answer.GetProperty("response").GetProperty("payLaterAgreed").GetBoolean());

        var cancelled = await (await trader.Client.CancelAsync(await trader.Client.MineAsync(request.Id()), "Đổi lịch")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(payLater, cancelled.GetProperty("response").GetProperty("payLaterAgreed").GetBoolean());
        Assert.False(cancelled.GetProperty("canCancel").GetBoolean());
    }

    [Theory]
    [InlineData("""{ "acceptedCages": 1, "pickupDate": "2099-01-01", "rowVersion": "AA==" }""")]
    [InlineData("""{ "acceptedCages": 1, "pickupDate": "2099-01-01", "payLaterAgreed": null, "rowVersion": "AA==" }""")]
    public async Task Pay_later_must_be_chosen(string body)
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);
        var withVersion = body.Replace("AA==", request.RowVersion());

        Assert.Equal(["Pay later"], await (await owner.PostAsJsonRawAsync($"/api/purchase-requests/{request.Id()}/accept", withVersion)).InvalidFieldsAsync());
    }

    // SALE-03.1: a decline saves only its note: no kg, no date, no agreement.
    [Fact]
    public async Task A_decline_holds_only_its_note()
    {
        var (owner, trader, productId) = await WorldAsync();
        var request = await trader.Client.PlaceAsync(productId);
        Assert.Equal(["Note"], await (await owner.DeclineAsync(request, null)).InvalidFieldsAsync());

        var response = (await (await owner.DeclineAsync(request, "  Chưa đủ cỡ  ")).ReadAsync(HttpStatusCode.OK)).GetProperty("response");

        Assert.Equal("Chưa đủ cỡ", response.Text("note"));
        Assert.Equal(JsonValueKind.Null, response.GetProperty("acceptedCages").ValueKind);
        Assert.Equal(JsonValueKind.Null, response.GetProperty("pickupDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, response.GetProperty("payLaterAgreed").ValueKind);
    }
}
