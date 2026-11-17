using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Dashboard.OperationsDashboardTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;

namespace OMTS.Api.IntegrationTests.Dashboard;

/// <summary>FARM-06: the Owner alone opens it, a filter that cannot be read is MSG-GEN-01, and every answer says when it was worked out.</summary>
public class OperationsDashboardAccessAndFilterTests
{
    [Fact]
    public async Task Only_the_cooperative_owner_opens_the_dashboard()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var accountant = await api.StaffAsync(null, StaffRole.Accountant);
        var allZonesManager = await api.StaffAsync(null, StaffRole.ZoneManager);
        var trader = await api.TraderAsync();

        Assert.Equal(HttpStatusCode.OK, (await sale.Owner.Client.GetDashboardAsync()).StatusCode);
        foreach (var client in new[] { sale.Manager.Client, accountant.Client, allZonesManager.Client, trader })
        {
            var refused = await client.GetDashboardAsync();
            Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetDashboardAsync()).StatusCode);
    }

    [Fact]
    public async Task The_answer_says_when_it_was_worked_out_and_echoes_the_zone()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);

        var dashboard = await sale.Owner.Client.DashboardAsync(Query(("zoneId", sale.ZoneId)));

        var computedAt = dashboard.GetProperty("computedAt").GetDateTimeOffset();
        Assert.InRange(computedAt, before, DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Equal(sale.ZoneId, dashboard.GetProperty("zoneId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await sale.Owner.Client.DashboardAsync()).GetProperty("zoneId").ValueKind);
    }

    // Figures are worked out from the records each time: a new invoice shows at once, with a later computedAt.
    [Fact]
    public async Task Nothing_is_stored_so_a_new_record_shows_in_the_next_answer()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var first = await sale.Owner.Client.DashboardAsync();

        await api.AddInvoiceAsync(sale.ZoneId, sale.Owner.Account.Id, 12_000m);
        var second = await sale.Owner.Client.DashboardAsync();

        Assert.Equal((0m, 12_000m), (first.RevenueTotal(), second.RevenueTotal()));
        Assert.True(second.GetProperty("computedAt").GetDateTimeOffset() >= first.GetProperty("computedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("from=abc")]
    [InlineData("to=2026-02-30")]
    [InlineData("from=2026-13-01")]
    [InlineData("zoneId=not-a-guid")]
    public async Task A_value_that_cannot_be_read_is_refused_with_msg_gen_01(string query)
    {
        using var api = new AuthApiFactory();
        var owner = await api.OwnerAsync();

        var response = await owner.Client.GetDashboardAsync("?" + query);

        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01"), (response.StatusCode, await response.ErrorCodeAsync()));
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_or_is_too_long_names_its_field()
    {
        using var api = new AuthApiFactory();
        var owner = (await api.OwnerAsync()).Client;

        var backwards = await owner.GetDashboardAsync(Query(("from", Today), ("to", Today.AddDays(-1))));
        var tooLong = await owner.GetDashboardAsync(Query(("from", Today.AddYears(-6)), ("to", Today)));
        var future = await owner.GetDashboardAsync(Query(("from", Today.AddDays(1))));

        foreach (var (response, field) in new[] { (backwards, "From date"), (tooLong, "To date"), (future, "From date") })
        {
            var problem = await response.ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", field), (problem.GetProperty("code").GetString(), problem.GetProperty("field").GetString()));
        }
    }

    [Fact]
    public async Task A_zone_that_does_not_exist_names_the_zone_field()
    {
        using var api = new AuthApiFactory();
        var owner = (await api.OwnerAsync()).Client;

        var response = await owner.GetDashboardAsync(Query(("zoneId", Guid.NewGuid())));

        var problem = await response.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Zone"), (problem.GetProperty("code").GetString(), problem.GetProperty("field").GetString()));
    }

    [Fact]
    public async Task Filters_sent_empty_are_no_filter_and_only_to_is_that_one_day()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.AddInvoiceAsync(sale.ZoneId, sale.Owner.Account.Id, 9_000m);
        await api.AddInvoiceAsync(sale.ZoneId, sale.Owner.Account.Id, 4_000m, Today.AddDays(-1));

        Assert.Equal(9_000m, (await sale.Owner.Client.DashboardAsync("?from=&to=&zoneId=")).RevenueTotal());
        Assert.Equal(4_000m, (await sale.Owner.Client.DashboardAsync(Query(("to", Today.AddDays(-1))))).RevenueTotal());
        Assert.Equal(13_000m, (await sale.Owner.Client.DashboardAsync(Query(("from", Today.AddDays(-1))))).RevenueTotal());
    }
}
