using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>One cycle grows one kind of oyster: the seed of every seeding session and nursery batch of the cycle sells as the product of the cycle, and a cycle with no product takes the one of its first seeding.</summary>
public sealed class CycleKindTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(7));
    private readonly Guid milky = Guid.NewGuid();
    private readonly Guid fat = Guid.NewGuid();

    private PurchaseReceiptLine Lot(Guid? seedProduct) => new()
    {
        LineNumber = 1,
        Receipt = new PurchaseReceipt { Code = "PN000001", ReceiptDate = new DateOnly(2026, 11, 1) },
        Item = new FarmingInputItem { Code = "GI000001", Name = "Hàu giống", Group = OMTS.Domain.Enums.InputItemGroup.Seed, ProductId = seedProduct },
    };

    [Fact]
    public void A_seed_of_the_product_of_the_cycle_matches_and_another_does_not()
    {
        Assert.True(CycleKindRules.Matches(milky, milky));
        Assert.False(CycleKindRules.Matches(milky, fat));
    }

    [Fact]
    public void A_seed_with_no_product_never_matches_a_cycle_that_has_one()
    {
        Assert.False(CycleKindRules.Matches(milky, null));
        Assert.Null(CycleKindRules.Refusal(milky, Lot(milky)));
        Assert.NotNull(CycleKindRules.Refusal(milky, Lot(null)));
    }

    [Fact]
    public void A_cycle_with_no_product_yet_accepts_any_seed()
    {
        Assert.True(CycleKindRules.Matches(null, fat));
        Assert.True(CycleKindRules.Matches(null, null));
        Assert.Null(CycleKindRules.Refusal(null, Lot(fat)));
    }

    [Fact]
    public void The_refusal_is_MSG_GEN_01_on_Seed_lot_with_the_hint()
    {
        var refused = CycleKindRules.Refusal(milky, Lot(fat))!;

        Assert.Equal((ErrorKind.Validation, "MSG-GEN-01"), (refused.Kind, refused.Message.Code));
        Assert.Equal(("Seed lot", "The seed grows another oyster than the cycle"), (refused.Details!["field"], refused.Details["hint"]));
    }

    [Fact]
    public void A_cycle_with_no_product_takes_the_one_of_the_seed_of_its_first_seeding_and_one_that_has_it_keeps_it()
    {
        Assert.Equal(fat, CycleKindRules.ProductFromFirstSeeding(null, Lot(fat)));
        Assert.Equal(milky, CycleKindRules.ProductFromFirstSeeding(milky, Lot(fat)));
        Assert.Null(CycleKindRules.ProductFromFirstSeeding(null, Lot(null)));
    }

    private SeedingSessionReader Reader() => new(
        StubProxy.Create<ISeedLotRepository>(_ => { }),
        StubProxy.Create<IRaftRepository>(h => h["GetForEditAsync"] = _ => new Raft { Code = "B1-01" }),
        // The lane states are read only once the lot is accepted, with lanes to judge; these sessions send none.
        null!,
        new Fakes.FixedTimeProvider(Now));

    private static CultivationCycle Cycle(Guid? product) => new() { ProductId = product, StartDate = new DateOnly(2026, 11, 5) };

    private static SeedingInput Session(PurchaseReceiptLine lot) => new(new DateOnly(2026, 11, 10), null, null, 100m, FixedLot: lot);

    [Fact]
    public async Task A_seeding_session_of_another_oyster_than_the_cycle_is_refused_on_the_seed_lot()
    {
        var (fields, error) = await Reader().ReadAsync(Cycle(milky), Session(Lot(fat)), null, null, reasonRequired: false, CancellationToken.None);

        Assert.Null(fields);
        Assert.Equal(("Seed lot", "The seed grows another oyster than the cycle"), (error!.Details!["field"], error.Details["hint"]));
    }

    [Fact]
    public async Task A_seeding_session_with_seed_that_has_no_product_is_refused_when_the_cycle_has_one()
    {
        var (_, error) = await Reader().ReadAsync(Cycle(milky), Session(Lot(null)), null, null, reasonRequired: false, CancellationToken.None);

        Assert.Equal("Seed lot", error!.Details!["field"]);
    }

    [Fact]
    public async Task A_seeding_session_of_the_cycle_product_or_of_a_cycle_with_no_product_goes_on_to_the_other_checks()
    {
        // It has no lanes, so the answer is the lanes, not the seed lot.
        var (_, sameKind) = await Reader().ReadAsync(Cycle(milky), Session(Lot(milky)), null, null, reasonRequired: false, CancellationToken.None);
        var (_, noProductYet) = await Reader().ReadAsync(Cycle(null), Session(Lot(fat)), null, null, reasonRequired: false, CancellationToken.None);

        Assert.Equal(["Lanes"], ((IReadOnlyList<string>)sameKind!.Details!["fields"]!).ToArray());
        Assert.Equal(["Lanes"], ((IReadOnlyList<string>)noProductYet!.Details!["fields"]!).ToArray());
    }

    [Fact]
    public async Task A_corrected_session_keeps_the_lot_it_has_but_cannot_move_to_another_oyster()
    {
        var own = Lot(fat);
        var record = new SeedingRecord { SeedLotLineId = own.Id, SeedLotLine = own };

        // The lot the session already has stays allowed, whatever the cycle holds now (older data).
        var (_, kept) = await Reader().ReadAsync(Cycle(milky), Session(own), record, "Sửa", reasonRequired: true, CancellationToken.None);
        // A lot the correction newly chooses must be of the cycle's oyster.
        var (_, moved) = await Reader().ReadAsync(Cycle(milky), Session(Lot(fat)), record, "Sửa", reasonRequired: true, CancellationToken.None);

        Assert.DoesNotContain("Seed lot", ((IReadOnlyList<string>)kept!.Details!["fields"]!));
        Assert.Equal("Seed lot", moved!.Details!["field"]);
    }
}
