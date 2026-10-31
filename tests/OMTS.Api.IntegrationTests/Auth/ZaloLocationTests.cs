using System.Net.Http.Json;
using System.Net;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

public sealed class ZaloLocationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Anonymous_cannot_exchange_location_token()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/me/zalo-location", Request("owner"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Trader_cannot_use_owner_location_input()
    {
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var client = await factory.LoginAsync(trader.Username!);
        var response = await client.PostAsJsonAsync("/api/me/zalo-location", Request("trader"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Linked_owner_resolves_position_without_saving_business_data()
    {
        var id = NewCode();
        var owner = await factory.AddAccountAsync(NewCode(), Password, configure: account => account.ZaloAccountId = id);
        var client = await factory.LoginAsync(owner.Username!);
        var response = await client.PostAsJsonAsync("/api/me/zalo-location", Request(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var position = await response.Content.ReadFromJsonAsync<Position>();
        Assert.Equal(21.123456m, position!.Latitude);
        Assert.Equal(107.456789m, position.Longitude);
    }

    [Fact]
    public async Task Another_zalo_identity_is_rejected()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password, configure: account => account.ZaloAccountId = NewCode());
        var client = await factory.LoginAsync(owner.Username!);
        var response = await client.PostAsJsonAsync("/api/me/zalo-location", Request(NewCode()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("expired")]
    [InlineData("fake:91,107")]
    [InlineData("fake:21,181")]
    public async Task Invalid_or_out_of_range_location_token_is_rejected(string token)
    {
        var id = NewCode();
        var owner = await factory.AddAccountAsync(NewCode(), Password, configure: account => account.ZaloAccountId = id);
        var client = await factory.LoginAsync(owner.Username!);
        var response = await client.PostAsJsonAsync("/api/me/zalo-location", new { zaloAccessToken = "fake:" + id, locationToken = token });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object Request(string id) => new { zaloAccessToken = "fake:" + id, locationToken = "fake:21.123456,107.456789" };
    private sealed record Position(decimal Latitude, decimal Longitude);
}
