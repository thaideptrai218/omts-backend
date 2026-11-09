using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using Catalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// TRACE-04 step 7 read again (QA M2): <c>GET api/harvest-lots/{id}/withdrawal-notices</c> answers the notices of the latest withdrawal as saved, with their delivery status and
/// <c>toCall</c>, so that a lost answer of the withdrawal does not lose the traders to call.
/// </summary>
public class ProvenanceWithdrawalNoticesTests
{
    [Fact]
    public async Task The_notices_of_the_latest_withdrawal_are_read_again_as_the_withdrawal_answered_them()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.EnsureCooperativeAsync();
        var told = await Catalogue.CreateCustomerAsync(sale.Owner.Client, name: "DEMO trader told");
        var refused = await Catalogue.CreateCustomerAsync(sale.Owner.Client, name: "DEMO trader refused");
        api.RefuseSmsTo(refused.Text("phoneNumber"));
        var walkIn = await sale.Manager.Client.SoldAsync(sale, sale.LotId, walkIn: true);
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: told.Id());
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: refused.Id());
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);

        // Never withdrawn: nothing to read again; an unknown lot is 404.
        var none = await sale.Owner.Client.WithdrawalNoticesAsync(sale.LotId);
        Assert.Equal((0, JsonValueKind.Null, "Published"), (none.GetProperty("notices").GetArrayLength(), none.GetProperty("withdrawnAt").ValueKind, none.Text("originState")));
        Assert.Equal(HttpStatusCode.NotFound, (await sale.Owner.Client.GetWithdrawalNoticesAsync(Guid.NewGuid())).StatusCode);

        var answer = await sale.Owner.Client.WithdrawnAsync(sale.LotId);
        var again = await sale.Owner.Client.WithdrawalNoticesAsync(sale.LotId);

        Assert.Equal(answer.GetProperty("notices").GetRawText(), again.GetProperty("notices").GetRawText());
        Assert.Equal((false, true), (again.NoticeOf(told.Id()).GetProperty("toCall").GetBoolean(), again.NoticeOf(refused.Id()).GetProperty("toCall").GetBoolean()));
        Assert.Equal(("Sent", "Not sent"), (again.NoticeOf(told.Id()).Text("deliveryStatus"), again.NoticeOf(refused.Id()).Text("deliveryStatus")));
        Assert.Equal([walkIn.Text("code")], again.Codes("walkInInvoiceCodes"));
        Assert.Equal("Withdrawn", again.Text("originState"));
        Assert.Equal((await api.LotRowAsync(sale.LotId)).WithdrawnAt, (DateTimeOffset?)again.GetProperty("withdrawnAt").GetDateTimeOffset());

        // Published again, then a new sale and a second withdrawal: the latest withdrawal is answered, also while the lot is Published again.
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Published", 2), ((await sale.Owner.Client.WithdrawalNoticesAsync(sale.LotId)).Text("originState"),
            (await sale.Owner.Client.WithdrawalNoticesAsync(sale.LotId)).GetProperty("notices").GetArrayLength()));
        var late = await Catalogue.CreateCustomerAsync(sale.Owner.Client, name: "DEMO trader late");
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, customerId: late.Id());
        var second = await sale.Owner.Client.WithdrawnAsync(sale.LotId);

        var latest = await sale.Owner.Client.WithdrawalNoticesAsync(sale.LotId);
        Assert.Equal(3, latest.GetProperty("notices").GetArrayLength());
        Assert.Equal(second.GetProperty("notices").GetRawText(), latest.GetProperty("notices").GetRawText());
    }
}
