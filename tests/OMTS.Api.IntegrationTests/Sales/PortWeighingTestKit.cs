using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>The weighing at the port: the staff records it, the trader confirms it, the invoice is made from it.</summary>
public static class PortWeighingTestKit
{
    public static object WeighedLot(Guid harvestLotId, params decimal[] cageKgs) => new { harvestLotId, cageKgs };

    /// <summary>
    /// Records lots on the sale's raft for the request, one per number of cages (two of eight when none are given), and marks its harvest done: a request is weighed only from
    /// its own lots after that (decision of 11/11/2026). Answers the setup with the first lots in place of the sale's own.
    /// </summary>
    public static async Task<SaleSetup> HarvestedForAsync(this SaleSetup sale, JsonElement request, params int[] cagesOfLots)
    {
        int[] counts = cagesOfLots.Length == 0 ? [8, 8] : cagesOfLots;
        var lots = new List<Guid>();
        // The lots of a request never exceed its accepted cages (13/11/2026): a small request the sale tests harvest more than it accepted is given room while the lots are recorded, then back its own cages.
        var accepted = await SetAcceptedCagesAsync(sale.Owner.Client, request.Id(), atLeast: counts.Sum());
        foreach (var cages in counts)
        {
            lots.Add((await sale.Owner.Client.HarvestLotAsync(sale.RaftId, new HarvestFields { Lanes = ["3"], Cages = cages.ToString() }.For(request.Id()))).Id());
        }

        await SetAcceptedCagesAsync(sale.Owner.Client, request.Id(), exactly: accepted);
        await sale.Owner.Client.MarkHarvestDoneAsync(request.Id());
        return sale with { LotId = lots[0], OtherLotId = lots.Count > 1 ? lots[1] : sale.OtherLotId };
    }

    /// <summary>Writes the accepted cages of a request straight to the database: at least <paramref name="atLeast"/>, or exactly <paramref name="exactly"/>. Answers what it was before.</summary>
    private static async Task<int?> SetAcceptedCagesAsync(HttpClient client, Guid requestId, int? atLeast = null, int? exactly = null)
    {
        using var scope = TestHosts.ServicesOf(client).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        var row = await db.PurchaseRequests.SingleAsync(r => r.Id == requestId);
        var before = row.AcceptedCages;
        row.AcceptedCages = atLeast is { } least ? Math.Max(before ?? 0, least) : exactly;
        await db.SaveChangesAsync();
        return before;
    }

    /// <summary>A line of an invoice made from a weighing: the kg is the weighed one.</summary>
    public static object WeighedLine(Guid productId, Guid lotId) => new { productId, harvestLotId = lotId };

    public static Task<HttpResponseMessage> PostWeighingAsync(this HttpClient staff, Guid requestId, params object[] lots) =>
        staff.PostAsJsonAsync("/api/port-weighings", new { purchaseRequestId = requestId, lots });

    public static async Task<JsonElement> WeighAsync(this HttpClient staff, Guid requestId, params object[] lots) =>
        await (await staff.PostWeighingAsync(requestId, lots)).ReadAsync(HttpStatusCode.Created);

    public static Task<HttpResponseMessage> PostConfirmAsync(this HttpClient trader, JsonElement weighing) =>
        trader.PostAsJsonAsync($"/api/me/port-weighings/{weighing.Id()}/confirm", new { rowVersion = weighing.RowVersion() });

    public static async Task<JsonElement> ConfirmWeighingAsync(this HttpClient trader, JsonElement weighing) =>
        await (await trader.PostConfirmAsync(weighing)).ReadAsync(HttpStatusCode.OK);

    /// <summary>A weighing of the request the trader has already confirmed.</summary>
    public static async Task<JsonElement> ConfirmedWeighingAsync(this HttpClient staff, HttpClient trader, Guid requestId, params object[] lots) =>
        await trader.ConfirmWeighingAsync(await staff.WeighAsync(requestId, lots));

    public static Dictionary<string, object?> FromWeighing(this Dictionary<string, object?> body, JsonElement weighing) =>
        body.With("purchaseRequestId", weighing.GetProperty("purchaseRequestId").GetGuid()).With("portWeighingId", weighing.Id());
}
