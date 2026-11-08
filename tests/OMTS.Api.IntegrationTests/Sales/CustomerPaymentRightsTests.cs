using System.Net;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>§I.4.4 row DEBT-02: Owner Full, Accountant Full (any pair: payments have no zone, BR-FARM-08), Zone Manager and trader No.</summary>
public class CustomerPaymentRightsTests
{
    [Fact]
    public async Task The_owner_and_the_accountant_of_any_zone_record_confirm_and_cancel_and_the_zone_manager_and_the_trader_are_refused()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var allowed = new[] { sale.Owner.Client, (await api.StaffAsync(zone, StaffRole.Accountant)).Client, (await api.StaffAsync(null, StaffRole.Accountant)).Client };
        var refused = new[] { sale.Manager.Client, (await api.StaffAsync(null, StaffRole.ZoneManager)).Client, (await api.TraderAsync()).Client };

        foreach (var client in allowed)
        {
            var awaiting = await api.PortCashOfAsync(await sale.PayLaterAsync(sale.CustomerId, 10m, cash: 100_000m));
            var paid = (await client.PaidAsync(MoneyBody(sale.CustomerId, 50_000m))).Payment().Id();
            Assert.Equal(HttpStatusCode.OK, (await client.GetFormAsync(sale.CustomerId)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetPaymentAsync(paid)).StatusCode);
            Assert.Contains(awaiting, (await client.AwaitingRowsAsync()).Select(r => r.Id()));
            Assert.Equal(HttpStatusCode.OK, (await client.ConfirmAsync([awaiting])).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.CancelAsync(paid, "Recorded twice")).StatusCode);
        }

        var payment = await api.PortCashOfAsync(await sale.PayLaterAsync(sale.CustomerId, 10m, cash: 100_000m));
        foreach (var client in refused)
        {
            foreach (var response in new[]
            {
                await client.GetFormAsync(sale.CustomerId), await client.GetPaymentAsync(payment), await client.GetAwaitingAsync(),
                await client.RecordAsync(MoneyBody(sale.CustomerId, 1_000m)), await client.PayBackAsync(MoneyBody(sale.CustomerId, 1_000m)),
                await client.ConfirmAsync([payment]), await client.CancelAsync(payment, "Not mine"),
            })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }
        }

        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAwaitingAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.RecordAsync(MoneyBody(sale.CustomerId, 1_000m))).StatusCode);
    }
}
