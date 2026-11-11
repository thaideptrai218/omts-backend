using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of OMTS-223: the UTC+7 day at its edges, in the rule and in the text of the messages. The services directly: a token does not outlive a clock set by hand.</summary>
public class PurchaseRequestQaBusinessDayTests
{
    private sealed class HandClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ClockedFactory(DateTimeOffset now) : AuthApiFactory
    {
        public HandClock Clock { get; } = new(now);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        }
    }

    // 16:59:59 UTC on the 6th is the last second of the 6th in Vietnam; one second later it is the 7th there, and still the 6th by UTC.
    [Fact]
    public async Task At_vietnam_midnight_the_day_turns_for_a_request()
    {
        await using var api = new ClockedFactory(new DateTimeOffset(2026, 11, 6, 16, 59, 59, TimeSpan.Zero));
        var (trader, _) = await api.LinkedProfileAsync("Chị Hoa");
        var product = new Product { Code = Guid.NewGuid().ToString("N")[..12], Name = NewCode(), UnitPrice = 1 };
        await api.UseDbAsync(db => db.Products.Add(product));
        var sixth = new DateOnly(2026, 11, 6);
        using var scope = api.Services.CreateScope();
        var traders = scope.ServiceProvider.GetRequiredService<TraderPurchaseRequestService>();

        Assert.True((await traders.PlaceAsync(trader.Id, new PlacePurchaseRequest(product.Id, 1m, sixth, null), CancellationToken.None)).IsSuccess);
        Assert.Equal("MSG-GEN-01", (await traders.PlaceAsync(trader.Id, new PlacePurchaseRequest(product.Id, 1m, sixth.AddDays(-1), null), CancellationToken.None)).Error!.Message.Code);

        api.Clock.Now = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);

        Assert.Equal("MSG-GEN-01", (await traders.PlaceAsync(trader.Id, new PlacePurchaseRequest(product.Id, 1m, sixth, null), CancellationToken.None)).Error!.Message.Code);
        Assert.True((await traders.PlaceAsync(trader.Id, new PlacePurchaseRequest(product.Id, 1m, sixth.AddDays(1), null), CancellationToken.None)).IsSuccess);
    }

    // {request_date} in the text, with an Owner to read it.
    [Fact]
    public async Task The_message_of_a_cancellation_reads_the_vietnam_day_the_request_was_sent()
    {
        await using var api = new ClockedFactory(new DateTimeOffset(2026, 11, 7, 17, 30, 0, TimeSpan.Zero));
        await api.AddAccountAsync(NewCode(), Password);
        var (trader, _) = await api.LinkedProfileAsync("Chị Hoa");
        var product = new Product { Code = "SP000001", Name = "Hàu sữa", UnitPrice = 1 };
        await api.UseDbAsync(db => db.Products.Add(product));
        using var scope = api.Services.CreateScope();
        var traders = scope.ServiceProvider.GetRequiredService<TraderPurchaseRequestService>();

        var placed = (await traders.PlaceAsync(trader.Id, new PlacePurchaseRequest(product.Id, 200m, new DateOnly(2026, 11, 8), null), CancellationToken.None)).Value!.Request;
        api.Clock.Now = new DateTimeOffset(2026, 11, 9, 2, 0, 0, TimeSpan.Zero);
        Assert.True((await traders.CancelAsync(trader.Id, placed.Id, new CancelPurchaseRequest("Đổi lịch", placed.RowVersion), CancellationToken.None)).IsSuccess);

        var row = (await api.NotificationRowsAsync(placed.Id)).OfType(NotificationTypes.PurchaseRequestCancelled).Single();
        Assert.Equal("Chị Hoa cancelled the purchase request of 08/11/2026 for about 200 kg of Hàu sữa. Đổi lịch", row.Body);
        Assert.Contains("\"request_date\":\"2026-11-08\"", row.MessageParams);
    }
}
