using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>§I.4.4 TRACE-04 and BR-TRACE-06: only the Cooperative Owner withdraws. The Zone Manager of the zone (R: sees the withdrawal on the trace), the Accountant and traders are refused.</summary>
public class ProvenanceWithdrawalAccessTests
{
    [Fact]
    public async Task Only_the_owner_previews_withdraws_and_reads_the_notices_again()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        var accountant = await api.StaffAsync(null, StaffRole.Accountant);
        var trader = await api.TraderAsync();

        foreach (var client in new[] { sale.Manager.Client, accountant.Client, trader })
        {
            foreach (var answer in new[] { await client.GetWithdrawPreviewAsync(sale.LotId), await client.PostWithdrawAsync(sale.LotId) })
            {
                Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (answer.StatusCode, await answer.ErrorCodeAsync()));
            }
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostWithdrawAsync(sale.LotId)).StatusCode);
        foreach (var client in new[] { sale.Manager.Client, accountant.Client })
        {
            Assert.False((await client.TraceAsync(sale.LotId)).GetProperty("canWithdraw").GetBoolean());
        }

        Assert.Equal(HarvestLotOriginState.Published, (await api.LotRowAsync(sale.LotId)).OriginState);
        Assert.Empty(await api.WithdrawAuditAsync(sale.LotId));
        Assert.Empty(await api.NoticeSendingsAsync(sale.LotId));

        await sale.Owner.Client.WithdrawnAsync(sale.LotId);

        // The notices of the withdrawal, read again: the Owner only, like the withdrawal.
        foreach (var client in new[] { sale.Manager.Client, accountant.Client, trader })
        {
            var notices = await client.GetWithdrawalNoticesAsync(sale.LotId);
            Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (notices.StatusCode, await notices.ErrorCodeAsync()));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetWithdrawalNoticesAsync(sale.LotId)).StatusCode);

        // R for the Zone Manager: the trace shows the lot Withdrawn with its date, never the reason.
        var seen = await sale.Manager.Client.TraceAsync(sale.LotId);
        Assert.Equal(("Withdrawn", false), (seen.Text("originState"), seen.GetProperty("canWithdraw").GetBoolean()));
        Assert.NotNull(seen.Text("withdrawnAt"));
        Assert.False(seen.TryGetProperty("withdrawalReason", out _));
    }
}
