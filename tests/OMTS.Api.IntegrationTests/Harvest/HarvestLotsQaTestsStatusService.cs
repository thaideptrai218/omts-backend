using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H1a: <see cref="HarvestLotStatusService"/> against the status group and transitions of BR-HARVEST-04, the illegal moves above all.</summary>
public class HarvestLotsQaTestsStatusService(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly DateOnly OnDay = new(2026, 10, 30);

    private async Task<Guid> NewLotAsync()
    {
        var raft = await factory.SeededRaftAsync();
        return (await raft.Owner.Client.HarvestLotAsync(raft.RaftId)).Id();
    }

    private async Task<T> OnLotAsync<T>(Guid lotId, Func<HarvestLotStatusService, HarvestLot, T> change)
    {
        using var scope = factory.Services.CreateScope();
        var lot = await scope.ServiceProvider.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lotId, CancellationToken.None);
        var result = change(scope.ServiceProvider.GetRequiredService<HarvestLotStatusService>(), lot!);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return result;
    }

    private Task<HarvestLot> ReadAsync(Guid lotId) => factory.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));

    private Task<int> AuditRowsAsync(Guid lotId) => factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == lotId && e.RecordType == "HarvestLot"));

    // No Open to Reopened, no Reopened to Reopened: Reopen answers false and writes nothing, also with the invoice given.
    [Theory]
    [InlineData(HarvestLotReopenCause.SalesReturnBackForSale)]
    [InlineData(HarvestLotReopenCause.SalesInvoiceCancelled)]
    [InlineData(HarvestLotReopenCause.SoldWholeTickRemoved)]
    public async Task Reopen_of_a_lot_that_is_not_closed_changes_nothing(HarvestLotReopenCause cause)
    {
        var lotId = await NewLotAsync();
        var invoice = Guid.NewGuid();
        var rows = await AuditRowsAsync(lotId);

        Assert.False(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, cause, invoice)));

        var open = await ReadAsync(lotId);
        Assert.Equal((StatusIds.HarvestLot.Open, (DateOnly?)null), (open.StatusId, open.ReopenedOn));
        Assert.Equal(rows, await AuditRowsAsync(lotId));

        Assert.Null(await OnLotAsync(lotId, (s, l) => s.CloseSoldWholeLot(l, OnDay, invoice)));
        Assert.True(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SalesReturnBackForSale)));
        rows = await AuditRowsAsync(lotId);

        Assert.False(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay.AddDays(1), cause, invoice)));

        var reopened = await ReadAsync(lotId);
        Assert.Equal((StatusIds.HarvestLot.Reopened, (DateOnly?)OnDay), (reopened.StatusId, reopened.ReopenedOn));
        Assert.Equal(rows, await AuditRowsAsync(lotId));
    }

    // Closed to Closed is illegal in every one of the ways, whatever closed it, and leaves the first close as it was.
    [Fact]
    public async Task A_closed_lot_cannot_be_closed_again_in_any_way_and_keeps_its_first_close()
    {
        var lotId = await NewLotAsync();
        var invoice = Guid.NewGuid();
        Assert.Null(await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 5m, HarvestLotCloseReason.Spoiled)));
        var rows = await AuditRowsAsync(lotId);

        var again = await OnLotAsync(lotId, (s, l) => new[]
        {
            s.Close(l, OnDay.AddDays(1), 0m, null),
            s.Close(l, OnDay.AddDays(1), 1m, HarvestLotCloseReason.TimeLimitReached),
            s.CloseSoldWholeLot(l, OnDay.AddDays(1), invoice),
            s.EnsureForSale([l]),
        });

        Assert.All(again, error => Assert.Equal("MSG-GEN-09", error!.Message.Code));
        var closed = await ReadAsync(lotId);
        Assert.Equal((OnDay, 5m, HarvestLotCloseReason.Spoiled, (Guid?)null), (closed.CloseDate!.Value, closed.LeftoverKg!.Value, closed.CloseReason!.Value, closed.ClosedBySalesInvoiceId));
        Assert.Equal(rows, await AuditRowsAsync(lotId));
    }

    // A Reopened lot is closed again in the same four ways, and each time the close facts of the last close are the ones it holds.
    [Fact]
    public async Task A_reopened_lot_closes_again_in_each_way_and_a_new_reopen_clears_the_close_facts()
    {
        var lotId = await NewLotAsync();
        var invoice = Guid.NewGuid();

        foreach (var close in new Func<HarvestLotStatusService, HarvestLot, object?>[]
        {
            (s, l) => s.CloseSoldWholeLot(l, OnDay, invoice),
            (s, l) => s.Close(l, OnDay, 3m, HarvestLotCloseReason.Unsold),
            (s, l) => s.Close(l, OnDay, 3m, HarvestLotCloseReason.TimeLimitReached),
            (s, l) => s.Close(l, OnDay, 0m, null),
        })
        {
            Assert.Null(await OnLotAsync(lotId, close));
            Assert.Equal(StatusIds.HarvestLot.Closed, (await ReadAsync(lotId)).StatusId);
            Assert.True(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay.AddDays(1), HarvestLotReopenCause.SalesReturnBackForSale)));

            var reopened = await ReadAsync(lotId);
            Assert.Equal(
                (StatusIds.HarvestLot.Reopened, (DateOnly?)OnDay.AddDays(1), (DateOnly?)null, (decimal?)null, (HarvestLotCloseReason?)null, (Guid?)null),
                (reopened.StatusId, reopened.ReopenedOn, reopened.CloseDate, reopened.LeftoverKg, reopened.CloseReason, reopened.ClosedBySalesInvoiceId));
            Assert.True(reopened.IsForSale);
        }
    }

    [Fact]
    public async Task Only_the_invoice_that_closed_the_lot_reopens_it_and_a_close_with_no_invoice_answers_to_none()
    {
        var lotId = await NewLotAsync();
        var (invoice, other) = (Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 1m, HarvestLotCloseReason.Unsold)));
        Assert.False(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SalesInvoiceCancelled, invoice)));
        Assert.False(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SoldWholeTickRemoved)));
        Assert.True(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SalesReturnBackForSale)));

        Assert.Null(await OnLotAsync(lotId, (s, l) => s.CloseSoldWholeLot(l, OnDay, invoice)));
        Assert.False(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SalesInvoiceCancelled, other)));
        Assert.False(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SoldWholeTickRemoved, other)));
        Assert.True(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SoldWholeTickRemoved, invoice)));
    }

    // GBR-02, GFR-05: outside a request the system is the actor (no account), and a named actor is kept. BR-HARVEST-04: the sold-whole reason is not for Close, a leftover above 0 needs one.
    [Fact]
    public async Task The_rules_of_a_close_and_the_actor_of_the_audit_row()
    {
        var lotId = await NewLotAsync();
        var named = await factory.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lotId).Select(l => l.RecordedByAccountId).SingleAsync());

        Assert.Equal("MSG-GEN-01", (await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 0m, HarvestLotCloseReason.SoldWholeLot)))!.Message.Code);
        Assert.Equal("MSG-GEN-01", (await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 1m, null)))!.Message.Code);
        Assert.Equal("MSG-GEN-01", (await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, -0.01m, HarvestLotCloseReason.Unsold)))!.Message.Code);
        Assert.Equal(StatusIds.HarvestLot.Open, (await ReadAsync(lotId)).StatusId);

        Assert.Null(await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 2m, HarvestLotCloseReason.TimeLimitReached)));
        var system = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == lotId && e.Action == HarvestLotAudit.Closed));
        Assert.Null(system.ActorAccountId);

        Assert.True(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SalesReturnBackForSale)));
        Assert.Null(await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 2m, HarvestLotCloseReason.Spoiled, named)));
        var actors = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == lotId && e.Action == HarvestLotAudit.Closed).Select(e => e.ActorAccountId).ToListAsync());
        Assert.Contains(named, actors.Cast<Guid?>());
    }

    // BR-HARVEST-04 asks for the reason "when the leftover kg is greater than 0" and says nothing of a reason with 0 (design Q3). Not graded: records what the branch does.
    [Fact]
    public async Task Observation_a_close_with_no_leftover_may_still_hold_a_reason()
    {
        var lotId = await NewLotAsync();

        Assert.Null(await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 0m, HarvestLotCloseReason.Unsold)));

        var closed = await ReadAsync(lotId);
        Assert.Equal((0m, HarvestLotCloseReason.Unsold), (closed.LeftoverKg!.Value, closed.CloseReason!.Value));
    }

    // BR-HARVEST-04: "Selling and closing are still allowed after the cycle of the lot is closed".
    [Fact]
    public async Task A_lot_of_a_closed_cycle_is_still_for_sale_closes_and_reopens_and_stays_readable()
    {
        var raft = await factory.SeededRaftAsync();
        var lotId = (await raft.Owner.Client.HarvestLotAsync(raft.RaftId)).Id();
        await factory.CloseCycleAsync(raft.CycleId);

        Assert.Null(await OnLotAsync(lotId, (s, l) => s.EnsureForSale([l])));
        Assert.Null(await OnLotAsync(lotId, (s, l) => s.Close(l, OnDay, 1m, HarvestLotCloseReason.Spoiled)));
        Assert.True(await OnLotAsync(lotId, (s, l) => s.Reopen(l, OnDay, HarvestLotReopenCause.SalesReturnBackForSale)));
        Assert.Equal("Reopened", (await raft.Owner.Client.GetHarvestLotAsync(lotId)).Text("status"));
    }

    // SCR-HARVEST-01 lists "the lots of its Open cycle", while a lot of a closed cycle is still for sale. Not graded: a question for H2, which adds Close lot. Records what the branch does.
    [Fact]
    public async Task Observation_a_lot_still_for_sale_after_the_close_is_not_on_the_screen_of_its_raft()
    {
        var raft = await factory.SeededRaftAsync();
        await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        await factory.CloseCycleAsync(raft.CycleId);

        Assert.Empty((await raft.Owner.Client.HarvestScreenAsync(raft.RaftId)).GetProperty("lots").EnumerateArray());
    }

    // BR-HARVEST-03: the first lot of a raft has no suffix. A raft code that holds a date reads as a suffix for the raft before it. Not graded, the code stays unique: records what the branch does.
    [Fact]
    public void Observation_a_raft_code_ending_in_the_date_of_today_makes_the_other_raft_jump_its_suffix() =>
        Assert.Equal("KV01-B01-20261026-20261027", HarvestLotCodes.Next("KV01-B01-20261026", ["KV01-B01-20261026-20261026"]));
}
