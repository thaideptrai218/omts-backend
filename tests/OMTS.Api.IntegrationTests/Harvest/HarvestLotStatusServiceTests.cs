using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>What SALE and CLAIM will call on a lot (BR-HARVEST-04, §8.2 of the design): the changes in the caller's save, with an audit row, and the "no sales yet" port.</summary>
public class HarvestLotStatusServiceTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly DateOnly OnDay = new(2026, 10, 30);
    private static readonly Guid Invoice = Guid.NewGuid();

    /// <summary>Runs a change on the tracked lot in one scope, and saves it as the caller would.</summary>
    private async Task<T> OnLotAsync<T>(Guid lotId, Func<HarvestLotStatusService, HarvestLot, T> change, bool save = true)
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var lot = await services.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lotId, CancellationToken.None);
        var result = change(services.GetRequiredService<HarvestLotStatusService>(), lot!);
        if (save)
        {
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        return result;
    }

    private Task<HarvestLot> ReadAsync(Guid lotId) => factory.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));

    private async Task<Guid> NewLotAsync()
    {
        var raft = await factory.SeededRaftAsync();
        return (await raft.Owner.Client.HarvestLotAsync(raft.RaftId)).Id();
    }

    [Fact]
    public async Task Sold_whole_closes_with_0_leftover_naming_the_invoice_and_only_that_invoice_reopens_it()
    {
        var lotId = await NewLotAsync();

        Assert.Null(await OnLotAsync(lotId, (s, l) => s.CloseSoldWholeLot(l, OnDay, Invoice)));

        var closed = await ReadAsync(lotId);
        Assert.Equal((StatusIds.HarvestLot.Closed, OnDay, 0m, HarvestLotCloseReason.SoldWholeLot, Invoice), (closed.StatusId, closed.CloseDate!.Value, closed.LeftoverKg!.Value, closed.CloseReason!.Value, closed.ClosedBySalesInvoiceId!.Value));
        Assert.False(closed.IsForSale);

        Assert.False(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay.AddDays(1), HarvestLotReopenCause.SalesInvoiceCancelled, Guid.NewGuid())));
        Assert.False(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay.AddDays(1), HarvestLotReopenCause.SoldWholeTickRemoved)));
        Assert.Equal(StatusIds.HarvestLot.Closed, (await ReadAsync(lotId)).StatusId);

        Assert.True(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay.AddDays(1), HarvestLotReopenCause.SoldWholeTickRemoved, Invoice)));

        var reopened = await ReadAsync(lotId);
        Assert.Equal((StatusIds.HarvestLot.Reopened, OnDay.AddDays(1)), (reopened.StatusId, reopened.ReopenedOn!.Value));
        Assert.Null(reopened.CloseDate);
        Assert.Null(reopened.LeftoverKg);
        Assert.Null(reopened.CloseReason);
        Assert.Null(reopened.ClosedBySalesInvoiceId);
        Assert.True(reopened.IsForSale);
    }

    // BR-HARVEST-04: "the earlier close stays in the audit log".
    [Fact]
    public async Task A_sales_return_reopens_a_lot_whatever_closed_it_and_the_audit_keeps_each_close()
    {
        var lotId = await NewLotAsync();
        Assert.Null(await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 12.5m, HarvestLotCloseReason.Unsold)));

        Assert.True(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SalesReturnBackForSale)));

        var rows = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == lotId).OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["HarvestLot.Created", "HarvestLot.Closed", "HarvestLot.Reopened"], rows.Select(e => e.Action));
        using var close = JsonDocument.Parse(rows[1].After!);
        Assert.Equal((12.5m, "Unsold", "CLOSED"), (close.RootElement.GetProperty("LeftoverKg").GetDecimal(), close.RootElement.Text("CloseReason"), close.RootElement.Text("Status")));
        Assert.Equal("SalesReturnBackForSale", rows[2].Reason);
        using var before = JsonDocument.Parse(rows[2].Before!);
        Assert.Equal(12.5m, before.RootElement.GetProperty("LeftoverKg").GetDecimal());

        // A Reopened lot is closed again in the same ways, and a lot not Closed does not reopen.
        Assert.False(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SalesReturnBackForSale), save: false));
        Assert.Null(await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 0m, null)));
        Assert.Null((await ReadAsync(lotId)).CloseReason);
    }

    [Fact]
    public async Task Close_refuses_a_lot_no_longer_for_sale_a_negative_leftover_and_a_leftover_with_no_reason()
    {
        var lotId = await NewLotAsync();

        AppError? Fields(HarvestLotStatusService s, HarvestLot l, decimal leftover, HarvestLotCloseReason? reason) => s.Close(l, OnDay, leftover, reason);
        Assert.Equal("Leftover kg", (await OnLotAsync(lotId, (s, l) => Fields(s, l, -1m, HarvestLotCloseReason.Unsold), save: false))!.Details!["field"]);
        Assert.Equal("Close reason", (await OnLotAsync(lotId, (s, l) => Fields(s, l, 5m, null), save: false))!.Details!["field"]);
        Assert.Equal("Close reason", (await OnLotAsync(lotId, (s, l) => Fields(s, l, 0m, HarvestLotCloseReason.SoldWholeLot), save: false))!.Details!["field"]);

        // The time limit of GFR-05 closes with the system as the actor, no account named.
        Assert.Null(await OnLotAsync(lotId, (s, l) => Fields(s, l, 400m, HarvestLotCloseReason.TimeLimitReached)));
        Assert.Equal(StatusIds.HarvestLot.Closed, (await ReadAsync(lotId)).StatusId);

        var again = await OnLotAsync(lotId, (s, l) => Fields(s, l, 0m, null), save: false);
        Assert.Equal(("MSG-GEN-09", ErrorKind.Conflict), (again!.Message.Code, again.Kind));
        var sold = await OnLotAsync(lotId, (s, l) => s.CloseSoldWholeLot(l, OnDay, Invoice), save: false);
        Assert.Equal("MSG-GEN-09", sold!.Message.Code);
    }

    [Fact]
    public async Task Ensure_for_sale_names_the_first_lot_no_longer_for_sale_and_passes_open_and_reopened_lots()
    {
        var (open, reopened, closed) = (await NewLotAsync(), await NewLotAsync(), await NewLotAsync());
        await factory.UpdateAsync<HarvestLot>(reopened, l => l.StatusId = StatusIds.HarvestLot.Reopened);
        await factory.UpdateAsync<HarvestLot>(closed, l => l.StatusId = StatusIds.HarvestLot.Closed);
        var code = (await ReadAsync(closed)).LotCode;

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<HarvestLotStatusService>();
        var lots = scope.ServiceProvider.GetRequiredService<IHarvestLotRepository>();
        var ofOpen = await lots.GetForEditAsync(open, CancellationToken.None);
        var ofReopened = await lots.GetForEditAsync(reopened, CancellationToken.None);
        var ofClosed = await lots.GetForEditAsync(closed, CancellationToken.None);

        Assert.Null(service.EnsureForSale([ofOpen!, ofReopened!]));

        var refused = service.EnsureForSale([ofOpen!, ofClosed!]);
        Assert.Equal(("MSG-GEN-09", $"Harvest lot {code} is Closed and cannot be used for this action."), (refused!.Message.Code, refused.Message.Text));
    }

    // TEMPORARY port, replaced by PR 6a: the true answer while no invoice exists.
    [Fact]
    public async Task The_no_sales_yet_port_answers_zero_for_every_lot_and_on_no_invoice()
    {
        var lotId = await NewLotAsync();
        using var scope = factory.Services.CreateScope();
        var sales = scope.ServiceProvider.GetRequiredService<IHarvestLotSalesQuery>();

        var figures = await sales.FiguresAsync([lotId, Guid.Empty, lotId], CancellationToken.None);

        Assert.Equal(LotSalesFigures.None, figures[lotId]);
        Assert.Equal(LotSalesFigures.None, figures[Guid.Empty]);
        Assert.Equal(2, figures.Count);
        Assert.False(await sales.IsOnAnyInvoiceAsync(lotId, CancellationToken.None));
    }

    // The screen and detail show the state a status change left, sale fields included.
    [Fact]
    public async Task A_closed_lot_shows_its_close_on_the_detail_and_comes_after_the_lots_for_sale()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);
        var other = await owner.HarvestLotAsync(raft.RaftId);
        Assert.Null(await OnLotAsync(other.Id(), (s, l) => s.Close(l, OnDay, 10m, HarvestLotCloseReason.Spoiled)));

        var detail = await owner.GetHarvestLotAsync(other.Id());

        Assert.Equal(("Closed", "Spoiled", 10m), (detail.Text("status"), detail.Text("closeReason"), detail.Kg("leftoverKg")));
        var figures = detail.GetProperty("figures");
        Assert.Equal((10m, 390m, false), (figures.Kg("lostKg"), figures.Kg("differenceKg"), figures.GetProperty("notFinal").GetBoolean()));
        Assert.Equal(lot.Id(), (await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("lots")[0].Id());
    }
}
