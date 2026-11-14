using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentSettlementTestKit;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>DEBT-02.6 refusals and who may settle; a refused list saves nothing.</summary>
public class CustomerPaymentSettlementRefusalTests
{
    // DEBT-02.6 step 2 and the refusals around it.
    [Fact]
    public async Task A_settlement_that_names_no_way_two_ways_or_an_unusable_profile_is_refused_and_nothing_is_saved()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var orphan = await api.OrphanTransferAsync(120_000m);

        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.SettleAsync(new { id = orphan.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.SettleAsync(new { id = orphan.Id, customerId = sale.CustomerId, payBack = new { method = "Cash" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.SettleAsync(new { id = orphan.Id, allocations = Array.Empty<object>(), payBack = new { method = "Cash" } })).StatusCode);
        Assert.Equal(["Customer"], await (await accountant.SettleAsync(ToCustomer(orphan.Id, Guid.NewGuid()))).InvalidFieldsAsync());
        Assert.Equal(["Customer"], await (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.Manager.Account.Id))).InvalidFieldsAsync());

        var mergedAway = (await sale.Owner.Client.CreateCustomerAsync()).Id();
        await api.UseDbAsync(db => db.Customers.Single(c => c.Id == mergedAway).MergedIntoCustomerId = sale.CustomerId);
        Assert.Equal("MSG-GEN-09", (await (await accountant.SettleAsync(ToCustomer(orphan.Id, mergedAway))).ReadAsync(HttpStatusCode.Conflict)).Text("code"));

        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, (await api.PaymentByIdAsync(orphan.Id)).StatusId);
        Assert.Empty(await api.PaidBackForAsync(orphan.Id));
        Assert.Empty(await api.AllocationsOfAsync(orphan.Id));
    }

    // A payment that is not marked has nothing to settle: one already on a profile, or cash at the port that names its invoice.
    [Fact]
    public async Task A_payment_that_is_not_marked_cannot_be_settled()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var onProfile = (await accountant.PaidAsync(MoneyBody(sale.CustomerId, 50_000m).With("confirmAboveDebt", true))).Payment().Id();
        var cash = await api.PortCashOfAsync(await sale.PayLaterAsync(sale.CustomerId, 10m, cash: 100_000m));

        foreach (var id in new[] { onProfile, cash })
        {
            var refused = await (await accountant.SettleAsync(ToCustomer(id, sale.CustomerId))).ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal("MSG-GEN-09", refused.Text("code"));
            Assert.Equal(id, refused.GetProperty("paymentId").GetGuid());
        }

        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, (await api.PaymentByIdAsync(cash)).StatusId);
    }

    // The list names each payment once, exists, and is not empty; a null entry is a field error, not a server error.
    [Fact]
    public async Task A_list_with_a_payment_twice_an_unknown_payment_or_a_null_entry_is_refused()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, accountant) = (w.Api, w.Sale, w.Accountant);
        var orphan = await api.OrphanTransferAsync(120_000m);

        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId), PayBack(orphan.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.ConfirmAsync([orphan.Id], (orphan.Id, "Not received"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await accountant.SettleAsync(ToCustomer(Guid.NewGuid(), sale.CustomerId))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.PostAsJsonAsync("/api/customer-payments/confirmations", new { settle = new object?[] { null } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.PostAsJsonAsync("/api/customer-payments/confirmations", new { settle = Array.Empty<object>() })).StatusCode);
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, (await api.PaymentByIdAsync(orphan.Id)).StatusId);
    }

    // §I.4.4 row DEBT-02: Owner and Accountant only; the right is CUSTOMER_PAYMENTS Edit.
    [Fact]
    public async Task Only_the_owner_and_the_accountant_settle()
    {
        using var w = await StartSettlementAsync();
        var (api, sale, _) = (w.Api, w.Sale, w.Accountant);
        var orphan = await api.OrphanTransferAsync(120_000m);
        foreach (var refused in new[] { sale.Manager.Client, (await api.TraderAsync()).Client })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await refused.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId))).StatusCode);
        }

        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId))).StatusCode);
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, (await api.PaymentByIdAsync(orphan.Id)).StatusId);
        Assert.Equal(HttpStatusCode.OK, (await sale.Owner.Client.SettleAsync(ToCustomer(orphan.Id, sale.CustomerId))).StatusCode);
    }
}
