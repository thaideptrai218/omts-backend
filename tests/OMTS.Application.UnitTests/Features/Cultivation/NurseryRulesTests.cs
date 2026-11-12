using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>The figures and checks of the nursery (v2, 13/11/2026) as pure functions: the loss, the pool of a batch and its hard cap, the loss of a cycle that shares a batch.</summary>
public class NurseryRulesTests
{
    private static NurseryBatch Batch(int status, decimal survivors, decimal? closeLoss = null) =>
        new() { Code = "NU000009", StatusId = status, QuantityIn = 10000m, Survivors = survivors, CloseLossPieces = closeLoss };

    // 10 000 pieces in, 9 500 survive: 500 lost, 5.0 % (an estimate: the survivors are counted by eye).
    [Theory]
    [InlineData(10000, 9500, 500, 5.0)]
    [InlineData(10000, 10000, 0, 0.0)]
    [InlineData(10000, 0, 10000, 100.0)]
    [InlineData(3, 2, 1, 33.3)]
    public void The_loss_is_what_went_in_less_the_survivors_with_one_decimal(double quantityIn, double survivors, double pieces, double percent)
    {
        Assert.Equal((decimal)pieces, NurseryRules.LossPieces((decimal)quantityIn, (decimal)survivors));
        Assert.Equal((decimal)percent, NurseryRules.LossPercent((decimal)quantityIn, (decimal)survivors));
    }

    [Fact]
    public void Nothing_put_in_has_no_loss_percentage()
    {
        Assert.Equal(0m, NurseryRules.LossPercent(0m, 0m));
        Assert.Equal(0m, NurseryRules.TotalLossPercent(0m, 0m, 0m));
    }

    // A closed batch adds what its close wrote off: 10 000 in, 9 500 survived, 1 500 were left in the pool: 2 000 lost, 20.0 %.
    [Fact]
    public void The_total_loss_of_a_closed_batch_adds_the_close_loss_to_the_nursery_loss()
    {
        Assert.Equal(2000m, NurseryRules.TotalLoss(10000m, 9500m, 1500m));
        Assert.Equal(20.0m, NurseryRules.TotalLossPercent(10000m, 9500m, 1500m));
        Assert.Equal(500m, NurseryRules.TotalLoss(10000m, 9500m, null));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0.01, true)]
    [InlineData(0.001, false)] // more than two decimals
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1000000001, false)]
    public void Pieces_are_above_zero_with_at_most_two_decimals(double quantity, bool valid)
    {
        Assert.Equal(valid, NurseryRules.IsPieces((decimal)quantity));
    }

    [Fact]
    public void Missing_pieces_are_not_valid_pieces()
    {
        Assert.False(NurseryRules.IsPieces(null));
    }

    // Survivors: from 0 to the pieces put in (0 means all died), never above them.
    [Theory]
    [InlineData(0, true)]
    [InlineData(9500, true)]
    [InlineData(10000, true)]
    [InlineData(10001, false)]
    [InlineData(-1, false)]
    public void The_survivors_are_between_zero_and_the_pieces_put_in(double survivors, bool valid)
    {
        Assert.Equal(valid, NurseryRules.IsSurvivors((decimal)survivors, 10000m));
    }

    [Fact]
    public void Missing_survivors_are_not_valid_survivors()
    {
        Assert.False(NurseryRules.IsSurvivors(null, 10000m));
    }

    // The pool: survivors less what every cycle seeded; a Closed batch has none left, a Nursing or Cancelled one has no pool.
    [Fact]
    public void The_pool_of_a_completed_batch_is_the_survivors_less_what_is_seeded_and_never_below_zero()
    {
        Assert.Equal(6500m, NurseryRules.PoolRemaining(Batch(StatusIds.NurseryBatch.Completed, 9500m), 3000m));
        Assert.Equal(0m, NurseryRules.PoolRemaining(Batch(StatusIds.NurseryBatch.Completed, 9500m), 9600m));
        Assert.Equal(0m, NurseryRules.PoolRemaining(Batch(StatusIds.NurseryBatch.Closed, 9500m, 6500m), 3000m));
        Assert.Null(NurseryRules.PoolRemaining(Batch(StatusIds.NurseryBatch.Nursing, 0m), 0m));
        Assert.Null(NurseryRules.PoolRemaining(Batch(StatusIds.NurseryBatch.Cancelled, 0m), 0m));
    }

    [Fact]
    public void Seeding_up_to_what_is_left_in_the_pool_is_allowed_and_one_piece_more_is_not()
    {
        var batch = Batch(StatusIds.NurseryBatch.Completed, 9500m);

        Assert.Null(NurseryRules.CheckPool(batch, 3000m, 6500m));
        var refused = NurseryRules.CheckPool(batch, 3000m, 6500.01m)!;

        Assert.Equal(ErrorKind.Conflict, refused.Kind);
        Assert.Equal(NurseryReasonCodes.PoolExceeded, refused.Details!["reasonCode"]);
        Assert.Equal(6500m, refused.Details["remaining"]);
        Assert.Equal(6500.01m, refused.Details["quantity"]);
        Assert.Equal(9500m, refused.Details["survivors"]);
        Assert.Equal(3000m, refused.Details["seeded"]);
    }

    // A record of a closed batch (seeded 3 000 of 9 500, 6 500 written off) can be kept or lowered, never raised.
    [Fact]
    public void A_closed_batch_gives_only_what_was_seeded_before_the_close()
    {
        var closed = Batch(StatusIds.NurseryBatch.Closed, 9500m, 6500m);

        Assert.Equal(3000m, NurseryRules.PoolSize(closed));
        Assert.Null(NurseryRules.CheckPool(closed, 0m, 3000m));
        Assert.NotNull(NurseryRules.CheckPool(closed, 0m, 3001m));
        Assert.NotNull(NurseryRules.CheckPool(closed, 3000m, 1m));
    }

    // Two cycles drew 3 000 and 1 000 from a batch of 10 000 in, 9 500 survivors, 1 500 closed off: the loss of 2 000 is shared 3:1, the percentage is the batch's.
    [Fact]
    public void The_nursery_figures_of_a_cycle_share_the_batch_by_what_each_cycle_seeded()
    {
        var batchId = Guid.NewGuid();

        var first = NurseryRules.SummaryOf(new NurseryCycleFacts(batchId, "NU000001", 10000m, 9500m, 1500m, 3000m, 4000m));
        var second = NurseryRules.SummaryOf(new NurseryCycleFacts(batchId, "NU000001", 10000m, 9500m, 1500m, 1000m, 4000m));

        Assert.Equal((7500m, 7125m, 1500m, 20.0m, true), (first.QuantityIn, first.Survivors, first.LossPieces, first.LossPercent, first.Estimated));
        Assert.Equal((2500m, 2375m, 500m, 20.0m), (second.QuantityIn, second.Survivors, second.LossPieces, second.LossPercent));
        Assert.Equal(2000m, first.LossPieces + second.LossPieces);
    }

    [Fact]
    public void A_cycle_that_seeded_nothing_has_no_share_of_the_batch()
    {
        var summary = NurseryRules.SummaryOf(new NurseryCycleFacts(Guid.NewGuid(), "NU000002", 10000m, 9500m, 0m, 0m, 0m));

        Assert.Equal((0m, 0m, 0m, 5.0m), (summary.QuantityIn, summary.Survivors, summary.LossPieces, summary.LossPercent));
    }

    [Fact]
    public void The_refusals_carry_a_reason_code_and_a_vietnamese_text()
    {
        var notNursing = NurseryRules.NotNursing("NU000003", "Đã ươm xong");
        var notCompleted = NurseryRules.NotCompleted("NU000003", "Đang ươm");
        var seedingCompleted = NurseryRules.SeedingCompletedRefusal("B01");

        Assert.Equal(NurseryReasonCodes.NotNursing, notNursing.Details!["reasonCode"]);
        Assert.Equal(NurseryReasonCodes.NotCompleted, notCompleted.Details!["reasonCode"]);
        Assert.Equal(NurseryReasonCodes.SeedingCompleted, seedingCompleted.Details!["reasonCode"]);
        Assert.Contains("NU000003", notNursing.Message.Text);
        Assert.Contains("B01", seedingCompleted.Message.Text);
    }
}
