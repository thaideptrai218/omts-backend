using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Seeding;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>Decision of 12/11/2026: the seed of a cycle against its planned seed quantity, the same thresholds as the harvest cap.</summary>
public class SeedingPlanRulesTests
{
    private static AppError? Check(decimal? planned, decimal others, decimal quantity, bool confirm = false, string? reason = null) =>
        SeedingPlanRules.Check(new SeedingPlanInput(planned, others, confirm, reason), quantity, out _);

    [Fact]
    public void No_plan_means_no_cap() => Assert.Null(Check(null, 1_000_000m, 1_000_000m));

    [Theory]
    [InlineData(100, 0, 110)]
    [InlineData(100, 60, 50)]
    [InlineData(100, 0, 1)]
    public void Up_to_110_percent_is_saved_without_a_question(decimal planned, decimal others, decimal quantity) => Assert.Null(Check(planned, others, quantity));

    [Fact]
    public void Between_110_and_150_percent_asks_and_150_exactly_is_still_confirmable()
    {
        Assert.Equal(ErrorKind.Conflict, Check(100m, 100m, 11m)!.Kind);
        Assert.Equal(ErrorKind.Conflict, Check(100m, 100m, 50m)!.Kind);
    }

    [Fact]
    public void Above_150_percent_is_refused_whatever_the_answer() =>
        Assert.Equal(ErrorKind.Validation, Check(100m, 100m, 50.01m, confirm: true, reason: "x")!.Kind);

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_confirmation_needs_a_reason(string? reason) => Assert.Equal(ErrorKind.Validation, Check(100m, 100m, 20m, confirm: true, reason: reason)!.Kind);

    [Fact]
    public void A_confirmed_session_returns_its_trimmed_reason()
    {
        Assert.Null(SeedingPlanRules.Check(new SeedingPlanInput(100m, 100m, true, "  Late order "), 20m, out var reason));
        Assert.Equal("Late order", reason);
    }

    [Theory]
    [InlineData(100, 115, 115.0)]
    [InlineData(300, 100, 33.3)]
    public void Percent_is_seeded_over_planned_with_one_decimal(decimal planned, decimal seeded, decimal percent) => Assert.Equal(percent, SeedingPlanRules.Percent(planned, seeded));

    [Fact]
    public void Percent_is_null_without_a_plan() => Assert.Null(SeedingPlanRules.Percent(null, 10m));
}
