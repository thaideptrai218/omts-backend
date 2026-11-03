using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>Regressions of the bugs QA found in the OMTS-209 review fixes.</summary>
public class ReviewFixQaRegressionTests
{
    [SqlServerFact]
    public async Task SQL_a_draft_edit_that_changes_only_lines_bumps_the_version_so_an_older_version_is_MSG06()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));
        var url = $"/api/purchase-receipts/{draft.Id()}";
        var read = draft.GetProperty("rowVersion").GetString();

        // First save changes only the lines (no receipt field), second save comes from the same old read.
        await (await world.Owner.PutAsJsonAsync(url, world.Body(new LineBody(world.Other, 2, 100_000)) with { RowVersion = read })).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.PutAsJsonAsync(url, world.Body(new LineBody(world.Other, 5, 100_000)) with { RowVersion = read }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        var current = await (await world.Owner.GetAsync(url)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2m, current.GetProperty("lines")[0].Money("quantity"));
    }

    [SqlServerFact]
    public async Task SQL_a_blank_rowVersion_on_a_draft_edit_is_MSG01_like_a_missing_one()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)));
        var url = $"/api/purchase-receipts/{draft.Id()}";

        var response = await world.Owner.PutAsJsonAsync(url, world.Body(new LineBody(world.Other, 2, 100_000)) with { RowVersion = string.Empty });
        Assert.Equal("RowVersion", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }
}
