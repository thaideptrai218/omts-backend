using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Nursery;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>The figures and checks of the nursery (ươm giống, decision of 12/11/2026) as pure functions: the loss, the shares of the survivors and the hard cap of seeding.</summary>
public class NurseryRulesTests
{
    private static NurseryAllocationInput Share(Guid raft, decimal quantity) => new(raft, quantity);

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
    public void Shares_that_add_up_to_the_survivors_exactly_are_fine_for_one_raft_or_many()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(NurseryRules.AllocationHint(9500m, [Share(a, 9500m)]));
        Assert.Null(NurseryRules.AllocationHint(9500m, [Share(a, 4750m), Share(b, 4750m)]));
        Assert.Null(NurseryRules.AllocationHint(0m, []));
    }

    [Fact]
    public void Shares_that_do_not_add_up_exactly_are_refused_with_a_hint_on_the_totals()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var hint = NurseryRules.AllocationHint(9500m, [Share(a, 4750m), Share(b, 4749m)]);

        Assert.NotNull(hint);
        Assert.Contains("9499", hint);
        Assert.Contains("9500", hint);
    }

    [Fact]
    public void Survivors_with_no_raft_to_farm_them_are_refused()
    {
        Assert.NotNull(NurseryRules.AllocationHint(9500m, []));
    }

    [Fact]
    public void A_raft_given_twice_or_with_no_piece_or_with_no_id_is_refused()
    {
        var a = Guid.NewGuid();

        Assert.NotNull(NurseryRules.AllocationHint(10m, [Share(a, 5m), Share(a, 5m)]));
        Assert.NotNull(NurseryRules.AllocationHint(10m, [Share(a, 10m), Share(Guid.NewGuid(), 0m)]));
        Assert.NotNull(NurseryRules.AllocationHint(10m, [new NurseryAllocationInput(null, 10m)]));
        Assert.NotNull(NurseryRules.AllocationHint(10m, [new NurseryAllocationInput(a, null)]));
        Assert.NotNull(NurseryRules.AllocationHint(10m, [new NurseryAllocationInput(a, 10.005m)]));
    }

    [Fact]
    public void What_can_still_be_hung_is_the_share_less_the_seed_hung_and_never_below_zero()
    {
        Assert.Equal(3000m, NurseryRules.Remaining(5000m, 2000m));
        Assert.Equal(0m, NurseryRules.Remaining(5000m, 5000m));
        Assert.Equal(0m, NurseryRules.Remaining(5000m, 5100m));
    }

    // Owner decision of 12/11/2026: seeding from a nursery is capped by the share of the cycle, exactly (no 110 % and 150 % of a plan).
    [Fact]
    public void Seeding_up_to_the_share_is_allowed_and_one_piece_above_is_a_conflict_with_the_figures()
    {
        Assert.Null(NurseryRules.CheckSeeding(4750m, 4000m, 750m));

        var over = NurseryRules.CheckSeeding(4750m, 4000m, 751m);

        Assert.NotNull(over);
        Assert.Equal(ErrorKind.Conflict, over.Kind);
        Assert.Equal(NurseryReasonCodes.SeedingAboveAllocation, over.Details!["reasonCode"]);
        Assert.Equal((4750m, 4000m, 750m, 751m), ((decimal)over.Details["allocated"]!, (decimal)over.Details["seeded"]!, (decimal)over.Details["remaining"]!, (decimal)over.Details["quantity"]!));
        Assert.Equal("Quantity", over.Details["field"]);
    }

    [Fact]
    public void Nothing_more_can_be_hung_from_a_share_already_fully_hung()
    {
        Assert.NotNull(NurseryRules.CheckSeeding(100m, 100m, 1m));
        Assert.NotNull(NurseryRules.CheckSeeding(100m, 120m, 1m));
    }

    // Owner decision of 13/11/2026: a session from a nursery hangs the whole share left; the quantity is left out or equals it (within three decimals), else 400 with the figures.
    [Fact]
    public void A_nursery_session_takes_the_whole_share_left_whether_the_quantity_is_left_out_or_equal()
    {
        Assert.Equal((4750m, (AppError?)null), NurseryRules.FullSeeding(4750m, 0m, null));
        Assert.Equal((750m, (AppError?)null), NurseryRules.FullSeeding(4750m, 4000m, 750m));
        Assert.Null(NurseryRules.FullSeeding(4750m, 0m, 4750.0004m).Error);
    }

    [Fact]
    public void A_nursery_session_with_another_quantity_is_a_bad_request_naming_the_full_share()
    {
        var refused = NurseryRules.FullSeeding(4750m, 0m, 4000m).Error;

        Assert.NotNull(refused);
        Assert.Equal(ErrorKind.Validation, refused.Kind);
        Assert.Equal(NurseryReasonCodes.SeedingMustUseFullAllocation, refused.Details!["reasonCode"]);
        Assert.Equal((4750m, 4000m), ((decimal)refused.Details["remaining"]!, (decimal)refused.Details["quantity"]!));
        Assert.Contains("còn 4750 mảnh", refused.Message.Text);
        Assert.NotNull(NurseryRules.FullSeeding(4750m, 0m, 4750.01m).Error);
    }

    [Fact]
    public void A_nursery_session_with_nothing_left_to_hang_is_a_bad_request_saying_it_is_complete()
    {
        var refused = NurseryRules.FullSeeding(4750m, 4750m, null).Error;

        Assert.NotNull(refused);
        Assert.Equal((ErrorKind.Validation, NurseryReasonCodes.SeedingAlreadyComplete), (refused.Kind, refused.Details!["reasonCode"]));
        Assert.Equal(0m, (decimal)refused.Details["remaining"]!);
    }

    // A batch shared by two rafts shows each cycle its part of the pieces put in, by the share of the survivors; the percentage is the batch's.
    [Fact]
    public void The_nursery_of_a_cycle_takes_its_part_of_the_pieces_put_in_by_its_share()
    {
        var facts = new NurseryCycleFacts(Guid.NewGuid(), "NU000001", 10000m, 9500m, 4750m, 1000m);

        var summary = NurseryRules.SummaryOf(facts);

        Assert.Equal((5000m, 4750m, 250m, 5.0m, true), (summary.QuantityIn, summary.Survivors, summary.LossPieces, summary.LossPercent, summary.Estimated));
        Assert.Equal("NU000001", summary.BatchCode);
    }

    [Fact]
    public void A_batch_with_one_raft_shows_its_whole_figures()
    {
        var summary = NurseryRules.SummaryOf(new NurseryCycleFacts(Guid.NewGuid(), "NU000002", 10000m, 9500m, 9500m, 0m));

        Assert.Equal((10000m, 9500m, 500m, 5.0m), (summary.QuantityIn, summary.Survivors, summary.LossPieces, summary.LossPercent));
    }

    [Fact]
    public void A_refusal_carries_its_reason_code_and_the_details_it_is_given()
    {
        var refused = NurseryRules.NotNursing("NU000003", "Đã hoàn tất");

        Assert.Equal(ErrorKind.Conflict, refused.Kind);
        Assert.Equal(NurseryReasonCodes.NotNursing, refused.Details!["reasonCode"]);
        Assert.Contains("NU000003", refused.Message.Text);
    }
}
