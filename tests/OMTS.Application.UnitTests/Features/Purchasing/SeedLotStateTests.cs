using OMTS.Application.Features.Purchasing.SeedLots;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>The state of a seed lot is worked out from its quantities, never stored: every state, the finals, and lots that took several paths.</summary>
public sealed class SeedLotStateTests
{
    private static SeedLotFigures Lot(
        decimal received = 100m, decimal returned = 0m, decimal nurseryIn = 0m, decimal nurseryLoss = 0m, decimal viaNursery = 0m, decimal direct = 0m, decimal issued = 0m) =>
        new(received, returned, nurseryIn, nurseryLoss, viaNursery, direct, issued);

    [Fact]
    public void A_lot_nothing_has_left_is_Unused_and_so_is_one_only_partly_returned()
    {
        Assert.Equal(SeedLotState.Unused, Lot().State);
        var partlyReturned = Lot(returned: 30m);
        Assert.Equal((SeedLotState.Unused, 70m, 70m), (partlyReturned.State, partlyReturned.Remaining, partlyReturned.Available));
    }

    [Fact]
    public void A_lot_with_pieces_in_the_nursery_is_Nursing()
    {
        var nursing = Lot(nurseryIn: 40m);

        Assert.Equal(SeedLotState.Nursing, nursing.State);
        // 40 are in the nursery, 60 are still in the stock; the lot keeps all 100.
        Assert.Equal((40m, 100m, 60m), (nursing.Nursing, nursing.Remaining, nursing.Available));
    }

    [Fact]
    public void A_completed_batch_with_pieces_left_in_its_pool_is_still_Nursing_and_the_pieces_that_died_are_lost()
    {
        // 40 went in, 30 survived, 10 were hung from the pool: 10 died, 20 wait in the pool.
        var lot = Lot(nurseryIn: 40m, nurseryLoss: 10m, viaNursery: 10m);

        Assert.Equal(SeedLotState.Nursing, lot.State);
        Assert.Equal((20m, 10m, 80m, 60m), (lot.Nursing, lot.Seeded, lot.Remaining, lot.Available));
    }

    [Fact]
    public void A_lot_partly_seeded_with_nothing_in_the_nursery_is_PartlySeeded()
    {
        var direct = Lot(direct: 60m);
        // 40 went into the nursery and all of them were dealt with: 5 died, 35 were hung. 60 are left in the stock, none in the nursery.
        var throughNursery = Lot(nurseryIn: 40m, nurseryLoss: 5m, viaNursery: 35m);

        Assert.Equal(SeedLotState.PartlySeeded, direct.State);
        Assert.Equal((40m, 40m), (direct.Remaining, direct.Available));
        Assert.Equal((SeedLotState.PartlySeeded, 0m, 60m), (throughNursery.State, throughNursery.Nursing, throughNursery.Remaining));
    }

    [Fact]
    public void A_lot_that_lost_pieces_or_gave_them_to_a_manual_issue_but_still_has_some_is_PartlySeeded()
    {
        Assert.Equal(SeedLotState.PartlySeeded, Lot(nurseryIn: 20m, nurseryLoss: 20m).State);
        Assert.Equal(SeedLotState.PartlySeeded, Lot(issued: 10m).State);
    }

    [Fact]
    public void A_lot_all_hung_is_FullySeeded_directly_or_through_the_nursery_with_its_losses()
    {
        var direct = Lot(direct: 100m);
        var viaNursery = Lot(nurseryIn: 100m, nurseryLoss: 8m, viaNursery: 92m);

        Assert.Equal((SeedLotState.FullySeeded, 0m), (direct.State, direct.Remaining));
        Assert.Equal((SeedLotState.FullySeeded, 0m, 100m), (viaNursery.State, viaNursery.Remaining, viaNursery.Seeded + viaNursery.NurseryLoss));
        Assert.True(SeedLotStates.IsFinal(direct.State));
    }

    [Fact]
    public void A_lot_hung_in_part_directly_and_in_part_from_the_nursery_is_FullySeeded_once_nothing_is_left()
    {
        var lot = Lot(nurseryIn: 30m, viaNursery: 30m, direct: 70m);

        Assert.Equal(SeedLotState.FullySeeded, lot.State);
        Assert.Equal(100m, lot.Seeded);
    }

    [Fact]
    public void A_lot_sent_back_whole_is_FullyReturned()
    {
        var lot = Lot(returned: 100m);

        Assert.Equal((SeedLotState.FullyReturned, 0m), (lot.State, lot.Remaining));
        Assert.True(SeedLotStates.IsFinal(lot.State));
    }

    [Fact]
    public void A_lot_lost_whole_in_the_nursery_or_written_off_is_FullyLost()
    {
        // The batch took 100, none survived (or the close wrote the pool off): nothing was hung.
        Assert.Equal(SeedLotState.FullyLost, Lot(nurseryIn: 100m, nurseryLoss: 100m).State);
        Assert.Equal(SeedLotState.FullyLost, Lot(issued: 100m).State);
        Assert.Equal(SeedLotState.FullyLost, Lot(nurseryIn: 60m, nurseryLoss: 60m, issued: 40m).State);
    }

    [Fact]
    public void A_lot_used_up_by_several_paths_is_UsedUp()
    {
        // Seeded and returned.
        Assert.Equal(SeedLotState.UsedUp, Lot(returned: 40m, direct: 60m).State);
        // Seeded and disposed.
        Assert.Equal(SeedLotState.UsedUp, Lot(direct: 90m, issued: 10m).State);
        // Returned and lost.
        Assert.Equal(SeedLotState.UsedUp, Lot(returned: 50m, nurseryIn: 50m, nurseryLoss: 50m).State);
        // Seeded through the nursery, returned, and some lost on the way.
        Assert.Equal(SeedLotState.UsedUp, Lot(returned: 20m, nurseryIn: 80m, nurseryLoss: 10m, viaNursery: 70m).State);
    }

    [Fact]
    public void A_lot_seeded_beyond_what_it_holds_has_nothing_left_never_below_zero()
    {
        // Seeding is not compared with the lot (GBR-04): the figures stay readable.
        var lot = Lot(received: 100m, direct: 130m);

        Assert.Equal((SeedLotState.FullySeeded, 0m, 0m), (lot.State, lot.Remaining, lot.Available));
    }

    [Fact]
    public void A_lot_with_no_quantity_at_all_is_Unused()
    {
        Assert.Equal(SeedLotState.Unused, Lot(received: 0m).State);
    }

    [Fact]
    public void The_nursery_never_holds_a_negative_quantity()
    {
        var lot = Lot(nurseryIn: 10m, nurseryLoss: 10m, viaNursery: 5m);

        Assert.Equal(0m, lot.Nursing);
    }

    [Theory]
    [InlineData(SeedLotState.Unused, false)]
    [InlineData(SeedLotState.Nursing, false)]
    [InlineData(SeedLotState.PartlySeeded, false)]
    [InlineData(SeedLotState.FullySeeded, true)]
    [InlineData(SeedLotState.FullyReturned, true)]
    [InlineData(SeedLotState.FullyLost, true)]
    [InlineData(SeedLotState.UsedUp, true)]
    public void Only_the_last_four_states_are_final(SeedLotState state, bool final) => Assert.Equal(final, SeedLotStates.IsFinal(state));

    [Fact]
    public void Every_final_state_has_nothing_remaining_and_no_other_state_has_nothing_remaining_unless_empty()
    {
        var lots = new[]
        {
            Lot(), Lot(returned: 30m), Lot(nurseryIn: 40m), Lot(nurseryIn: 40m, nurseryLoss: 10m, viaNursery: 10m), Lot(direct: 60m), Lot(issued: 10m),
            Lot(direct: 100m), Lot(returned: 100m), Lot(nurseryIn: 100m, nurseryLoss: 100m), Lot(returned: 40m, direct: 60m),
        };

        foreach (var lot in lots)
        {
            Assert.Equal(SeedLotStates.IsFinal(lot.State), lot.Remaining == 0m);
        }
    }

    [Fact]
    public void A_seed_receipt_shows_the_state_its_lots_share_else_Nursing_else_PartlySeeded_else_UsedUp()
    {
        Assert.Equal(SeedLotState.Unused, SeedLotStates.Combine([]));
        Assert.Equal(SeedLotState.FullySeeded, SeedLotStates.Combine([SeedLotState.FullySeeded, SeedLotState.FullySeeded]));
        Assert.Equal(SeedLotState.Nursing, SeedLotStates.Combine([SeedLotState.Unused, SeedLotState.Nursing, SeedLotState.FullySeeded]));
        Assert.Equal(SeedLotState.PartlySeeded, SeedLotStates.Combine([SeedLotState.Unused, SeedLotState.FullySeeded]));
        Assert.Equal(SeedLotState.PartlySeeded, SeedLotStates.Combine([SeedLotState.PartlySeeded, SeedLotState.Unused]));
        Assert.Equal(SeedLotState.UsedUp, SeedLotStates.Combine([SeedLotState.FullySeeded, SeedLotState.FullyReturned]));
    }
}
