using OMTS.Application.Common.Results;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>A harvest lot holds one product, the oyster of its cycle: at most one line, of the product of the cycle, and the line is built from the cages and kg when the form sends none.</summary>
public sealed class HarvestSingleProductTests
{
    private readonly Product milky = new() { Code = "SP000001", Name = "Hàu sữa" };
    private readonly Product fat = new() { Code = "SP000002", Name = "Hàu béo" };

    private static ResolvedProductLine Line(Product product, decimal cages = 5m, decimal kg = 125m) => new(product, cages, kg);

    private CultivationCycle Cycle(Product? product) => new() { ProductId = product?.Id, Product = product };

    [Fact]
    public void A_lot_with_no_line_or_with_one_line_of_the_cycle_product_matches()
    {
        Assert.True(HarvestProductMatch.Matches(milky.Id, null));
        Assert.True(HarvestProductMatch.Matches(milky.Id, []));
        Assert.True(HarvestProductMatch.Matches(milky.Id, [Line(milky)]));
    }

    [Fact]
    public void A_line_of_another_product_does_not_match_a_cycle_that_has_one()
    {
        Assert.False(HarvestProductMatch.Matches(milky.Id, [Line(fat)]));
    }

    [Fact]
    public void A_cycle_with_no_product_takes_one_line_of_any_product()
    {
        Assert.True(HarvestProductMatch.Matches(null, [Line(fat)]));
    }

    [Fact]
    public void A_lot_never_holds_two_lines_even_when_one_is_the_product_of_the_cycle()
    {
        Assert.False(HarvestProductMatch.Matches(milky.Id, [Line(milky), Line(fat)]));
        Assert.False(HarvestProductMatch.Matches(null, [Line(milky), Line(fat)]));
    }

    [Fact]
    public void Two_lines_are_refused_with_MSG_GEN_01_on_the_product_lines()
    {
        var refused = HarvestProductMatch.Refusal(Cycle(milky), [Line(milky), Line(fat)])!;

        Assert.Equal((ErrorKind.Validation, "MSG-GEN-01"), (refused.Kind, refused.Message.Code));
        Assert.Equal(("Product lines", HarvestProductMatch.TooManyLinesHint), (refused.Details!["field"], refused.Details["hint"]));
    }

    [Fact]
    public void A_line_of_another_oyster_than_the_cycle_is_refused_with_the_reason_code_and_the_name_of_the_cycle_product()
    {
        var refused = HarvestProductMatch.Refusal(Cycle(milky), [Line(fat)])!;

        Assert.Equal((ErrorKind.Validation, "MSG-GEN-09"), (refused.Kind, refused.Message.Code));
        Assert.Equal((HarvestProductMatch.ReasonCode, milky.Id, "Hàu sữa"), (refused.Details!["reasonCode"], refused.Details["cycleProductId"], refused.Details["cycleProductName"]));
    }

    [Fact]
    public void One_line_of_the_cycle_product_a_lot_with_no_line_and_a_cycle_with_no_product_are_not_refused()
    {
        Assert.Null(HarvestProductMatch.Refusal(Cycle(milky), [Line(milky)]));
        Assert.Null(HarvestProductMatch.Refusal(Cycle(milky), null));
        Assert.Null(HarvestProductMatch.Refusal(Cycle(null), [Line(fat)]));
    }

    [Fact]
    public void A_form_that_sends_no_line_gets_one_made_of_its_cages_and_kg_for_the_cycle_product()
    {
        var withKg = HarvestLotRecordingService.WholeLot(new RecordHarvestRequest([1, 2], [], 6m, 150m), milky.Id)!;
        var withoutKg = HarvestLotRecordingService.WholeLot(new RecordHarvestRequest([1, 2], [], 6m, null), milky.Id)!;

        Assert.Equal(new HarvestProductLineInput(milky.Id, 6m, 150m), Assert.Single(withKg));
        Assert.Equal(new HarvestProductLineInput(milky.Id, 6m, null), Assert.Single(withoutKg));
    }

    [Fact]
    public void No_line_is_made_when_the_cages_are_missing_or_a_value_could_not_be_read()
    {
        Assert.Null(HarvestLotRecordingService.WholeLot(new RecordHarvestRequest([1], [], null, 150m), milky.Id));
        Assert.Null(HarvestLotRecordingService.WholeLot(new RecordHarvestRequest([1], [], 6m, null, EstimatedKgIsUnreadable: true), milky.Id));
        Assert.Null(HarvestLotRecordingService.WholeLot(new RecordHarvestRequest([1], [], 6m, 150m, ProductLinesAreUnreadable: true), milky.Id));
    }
}
