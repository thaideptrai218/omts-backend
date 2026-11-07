using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Platform;
using OMTS.Infrastructure.Jobs;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-02 calls and GFR-05 runs, and what they should have left behind.</summary>
public static class HarvestLotCloseTestKit
{
    public const string WarningType = "HARVEST_LOT_AUTO_CLOSE_WARNING";

    public static Task<HttpResponseMessage> PostCloseLotAsync(this HttpClient client, Guid lotId, decimal? leftoverKg, string? reason, string? rowVersion) =>
        client.PostAsJsonAsync($"/api/harvest-lots/{lotId}/close", new { leftoverKg, reason, rowVersion });

    /// <summary>Sends a close with the current version of the lot.</summary>
    public static async Task<HttpResponseMessage> SendCloseAsync(this AuthApiFactory factory, HttpClient client, Guid lotId, decimal? leftoverKg, string? reason = null) =>
        await client.PostCloseLotAsync(lotId, leftoverKg, reason, await factory.LotVersionAsync(lotId));

    /// <summary>A close that must be saved: the whole answer.</summary>
    public static async Task<JsonElement> CloseLotAsync(this AuthApiFactory factory, HttpClient client, Guid lotId, decimal leftoverKg, string? reason = null) =>
        await (await factory.SendCloseAsync(client, lotId, leftoverKg, reason)).ReadAsync(HttpStatusCode.OK);

    /// <summary>One run of the daily close check on <paramref name="today"/>.</summary>
    public static Task<HarvestLotAutoCloseRun> AutoCloseOnAsync(this WebApplicationFactory<Program> api, DateOnly today) =>
        api.Services.GetRequiredService<HarvestLotAutoCloseJob>().RunOnceAsync(today);

    public static Task<HarvestLot> LotRowAsync(this AuthApiFactory factory, Guid lotId) =>
        factory.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));

    public static Task<List<AuditEvent>> CloseRowsAsync(this AuthApiFactory factory, Guid lotId) =>
        factory.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == lotId && e.Action == HarvestLotAudit.Closed).ToListAsync());

    public static Task<List<Notification>> WarningsAsync(this AuthApiFactory factory, Guid lotId) => factory.NoticesAsync(WarningType, lotId);

    /// <summary>Runs a status change on the tracked lot in one scope and saves it, as SALE and CLAIM do.</summary>
    public static async Task OnLotAsync(this AuthApiFactory factory, Guid lotId, Action<HarvestLotStatusService, HarvestLot> change)
    {
        using var scope = factory.Services.CreateScope();
        var lot = await scope.ServiceProvider.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lotId, CancellationToken.None);
        change(scope.ServiceProvider.GetRequiredService<HarvestLotStatusService>(), lot!);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    /// <summary>A sales return puts oysters back into the Closed lot on <paramref name="on"/> (CLAIM-03): it is Reopened from that day.</summary>
    public static Task ReopenAsync(this AuthApiFactory factory, Guid lotId, DateOnly on) =>
        factory.OnLotAsync(lotId, (s, l) => Assert.True(s.Reopen(l, on, HarvestLotReopenCause.SalesReturnBackForSale)));
}
