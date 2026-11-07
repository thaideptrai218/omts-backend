using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: the configured SMS sender behind ITraderMessageSender: Fake, None and an unknown name.</summary>
public class PurchaseRequestQaSmsProviderTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Sms:Provider=None: Not sent, NOT_CONFIGURED, and the log masks the number and holds no text.
    [Fact]
    public async Task With_none_the_response_is_saved_not_sent_and_the_log_holds_neither_the_number_nor_the_text()
    {
        var ownerAccount = await factory.AddAccountAsync(NewCode(), Password);
        await factory.EnsureCooperativeAsync();
        var product = await (await factory.LoginAsync(ownerAccount.Username)).CreateProductAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(product.Id());
        await using var none = factory.WithWebHostBuilder(b => b.UseSetting("Sms:Provider", "None"));
        var client = none.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await LoginTokensAsync(client, ownerAccount.Username)).GetProperty("accessToken").GetString());

        var answer = await (await client.DeclineAsync(request, "Ghi-chu-khong-duoc-vao-log")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Not sent", answer.GetProperty("sendings")[0].Text("deliveryStatus"));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("Ghi-chu-khong-duoc-vao-log", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains(trader.Profile.PhoneNumber!, StringComparison.Ordinal));
    }

    // The Fake keeps the very text of the row, for the very number of the profile.
    [Fact]
    public async Task With_the_fake_the_inbox_holds_what_the_row_says()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync((await owner.CreateProductAsync()).Id());

        (await owner.AcceptAsync(request)).EnsureSuccessStatusCode();

        var row = (await factory.NotificationRowsAsync(request.Id())).Sendings().Single();
        var message = Assert.Single(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(trader.Profile.PhoneNumber!));
        Assert.Equal((row.Body, trader.Profile.PhoneNumber), (message.Text, row.PhoneNumber));
    }
}
