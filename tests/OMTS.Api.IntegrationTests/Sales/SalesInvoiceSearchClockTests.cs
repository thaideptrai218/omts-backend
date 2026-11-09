using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-09 step 1 at the edge of the UTC+7 day. The service directly: a token does not outlive a clock set by hand.</summary>
public class SalesInvoiceSearchClockTests
{
    private sealed class HandClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedUser(Guid accountId) : ICurrentUser
    {
        public Guid? AccountId => accountId;

        public Guid? SessionId => null;
    }

    private sealed class ClockedFactory(DateTimeOffset now, Func<Guid> accountId) : AuthApiFactory
    {
        public HandClock Clock { get; } = new(now);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.RemoveAll<ICurrentUser>();
                services.AddScoped<ICurrentUser>(_ => new FixedUser(accountId()));
            });
        }
    }

    // 16:59:59 UTC on the 6th is the last second of the 6th in Vietnam; one second later it is the 7th there, and still the 6th by UTC.
    [Fact]
    public async Task At_vietnam_midnight_the_default_list_moves_to_the_next_day_and_the_invoice_stays_on_its_own_day()
    {
        var sixth = new DateOnly(2026, 11, 6);
        var ownerId = Guid.Empty;
        await using var api = new ClockedFactory(new DateTimeOffset(2026, 11, 6, 16, 59, 59, TimeSpan.Zero), () => ownerId);
        await api.EnsureCooperativeAsync();
        var owner = await api.AddAccountAsync(NewCode(), NewPassword());
        ownerId = owner.Id;
        var zone = await api.AddZoneAsync(NewCode());
        var invoice = new SalesInvoice
        {
            Code = "HD" + NewCode(), InvoicedAt = api.Clock.GetUtcNow(), InvoiceDate = sixth, ZoneId = zone.Id, Subtotal = 100_000m, Total = 100_000m,
            PaymentTerm = SalesPaymentTerm.PayLater, CreatedByAccountId = owner.Id,
        };
        await api.UseDbAsync(db => db.SalesInvoices.Add(invoice));

        var lastSecond = await SearchAsync(api, new SalesInvoiceSearchQuery());

        api.Clock.Now = new DateTimeOffset(2026, 11, 6, 17, 0, 0, TimeSpan.Zero);
        var nextDay = await SearchAsync(api, new SalesInvoiceSearchQuery());
        var onItsDay = await SearchAsync(api, new SalesInvoiceSearchQuery(From: sixth, To: sixth));

        Assert.Equal((sixth, sixth, invoice.Code), (lastSecond.From, lastSecond.To, Assert.Single(lastSecond.Page.Items).Code));
        Assert.Equal((sixth.AddDays(1), sixth.AddDays(1)), (nextDay.From, nextDay.To));
        Assert.Empty(nextDay.Page.Items);
        Assert.Equal("MSG-GEN-10", nextDay.MessageCode);
        Assert.Equal(invoice.Code, Assert.Single(onItsDay.Page.Items).Code);
    }

    private static async Task<SalesInvoiceSearchResult> SearchAsync(AuthApiFactory api, SalesInvoiceSearchQuery query)
    {
        using var scope = api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<SalesInvoiceSearchService>().SearchAsync(query, CancellationToken.None)).Value!;
    }
}
