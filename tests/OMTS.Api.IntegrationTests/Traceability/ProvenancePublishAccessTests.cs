using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>§I.4.4 TRACE-01 (Owner Full, Zone Manager Full in the zone, Accountant No): who may check and publish a lot, and the check of an invoice.</summary>
public class ProvenancePublishAccessTests
{
    [Fact]
    public async Task An_accountant_and_a_zone_manager_of_another_zone_are_refused_and_nothing_changes()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var accountant = await api.StaffAsync(null, StaffRole.Accountant);
        var stranger = await api.ManagerAsync((await sale.Owner.Client.CreateZoneAsync()).Id());

        foreach (var client in new[] { accountant.Client, stranger.Client })
        {
            foreach (var answer in new[] { await client.GetPublishCheckAsync(sale.LotId), await client.PostPublishAsync(sale.LotId), await client.GetInvoicePublishCheckAsync(invoice.Id()) })
            {
                Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (answer.StatusCode, await answer.ErrorCodeAsync()));
            }
        }

        Assert.Equal(HarvestLotOriginState.NotPublished, (await api.LotRowAsync(sale.LotId)).OriginState);
        Assert.Empty(await api.PublishAuditAsync(sale.LotId));
    }

    [Fact]
    public async Task Unknown_lots_and_invoices_answer_404_and_a_cancelled_invoice_is_not_checked()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        foreach (var answer in new[] { await sale.Owner.Client.GetPublishCheckAsync(Guid.NewGuid()), await sale.Owner.Client.PostPublishAsync(Guid.NewGuid()),
                     await sale.Owner.Client.GetInvoicePublishCheckAsync(Guid.NewGuid()) })
        {
            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        }

        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await api.CancelInvoiceAsync(invoice.Id());
        Assert.Equal("MSG-GEN-09", await (await sale.Manager.Client.GetInvoicePublishCheckAsync(invoice.Id())).ErrorCodeAsync());
    }
}
