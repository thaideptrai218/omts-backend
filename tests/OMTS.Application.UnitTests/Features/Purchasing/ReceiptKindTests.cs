using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>A receipt is a Seed receipt or a Supply receipt, fixed when it is created: it never mixes seed items with the others, on a Draft, on completion and on an edit.</summary>
public sealed class ReceiptKindTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(7));

    private readonly FarmingInputItem seed = new() { Code = "GI000001", Name = "Hàu giống", Group = InputItemGroup.Seed, Unit = "con" };
    private readonly FarmingInputItem rope = new() { Code = "VT000001", Name = "Dây", Group = InputItemGroup.Material, Unit = "m" };
    private readonly FarmingInputItem fuel = new() { Code = "HK000001", Name = "Dầu", Group = InputItemGroup.Other, Unit = "lít" };
    private readonly Supplier supplier = new() { Name = "Trại giống" };

    private PurchaseReceiptValidator Validator()
    {
        var items = new[] { seed, rope, fuel };
        var guard = new ZoneAccessGuard(new Fakes.FakeCurrentUser(null), null!);
        return new PurchaseReceiptValidator(
            StubProxy.Create<ISupplierRepository>(h => h["GetForEditAsync"] = _ => supplier),
            StubProxy.Create<IRaftRepository>(h => h["GetByIdsAsync"] = _ => new List<Raft>()),
            StubProxy.Create<IFarmingInputItemRepository>(h => h["GetByIdsAsync"] = a => items.Where(i => ((IReadOnlyCollection<Guid>)a[0]!).Contains(i.Id)).ToList()),
            StubProxy.Create<IFarmingZoneRepository>(_ => { }),
            StubProxy.Create<IAccountRepository>(_ => { }),
            new PurchaseReceiptAccess(guard, new Fakes.FakeCurrentUser(null), null!),
            new Fakes.FixedTimeProvider(Now));
    }

    private SavePurchaseReceiptRequest Request(PurchaseReceiptKind? kind, params FarmingInputItem[] lines) => new(
        supplier.Id, null, null, lines.Select(i => new ReceiptLineInput(i.Id, 10m, 1_000m)).ToList(), Kind: kind);

    [Fact]
    public void A_new_receipt_takes_the_kind_sent_else_that_of_its_first_item_else_Supply()
    {
        Assert.Equal(PurchaseReceiptKind.Supply, ReceiptKindRules.ForNew(PurchaseReceiptKind.Supply, [InputItemGroup.Seed]));
        Assert.Equal(PurchaseReceiptKind.Seed, ReceiptKindRules.ForNew(null, [null, InputItemGroup.Seed, InputItemGroup.Material]));
        Assert.Equal(PurchaseReceiptKind.Supply, ReceiptKindRules.ForNew(null, [InputItemGroup.Other, InputItemGroup.Seed]));
        Assert.Equal(PurchaseReceiptKind.Supply, ReceiptKindRules.ForNew(null, []));
        Assert.Equal(PurchaseReceiptKind.Supply, ReceiptKindRules.ForNew(null, [null]));
    }

    [Theory]
    [InlineData(InputItemGroup.Seed, PurchaseReceiptKind.Seed)]
    [InlineData(InputItemGroup.Material, PurchaseReceiptKind.Supply)]
    [InlineData(InputItemGroup.Other, PurchaseReceiptKind.Supply)]
    public void Material_and_Other_items_belong_to_Supply_and_Seed_items_to_Seed(InputItemGroup group, PurchaseReceiptKind kind) =>
        Assert.Equal(kind, ReceiptKindRules.KindOf(group));

    [Fact]
    public void A_seed_receipt_holds_only_seed_items_and_a_supply_receipt_holds_no_seed_item()
    {
        var seedWithRope = ReceiptKindRules.Mismatch(PurchaseReceiptKind.Seed, [InputItemGroup.Seed, InputItemGroup.Material]);
        var supplyWithSeed = ReceiptKindRules.Mismatch(PurchaseReceiptKind.Supply, [InputItemGroup.Material, InputItemGroup.Seed]);

        Assert.Equal(ErrorKind.Validation, seedWithRope!.Kind);
        Assert.Equal(("Lines", "A seed receipt holds only seed items"), (seedWithRope.Details!["field"], seedWithRope.Details["hint"]));
        Assert.Equal(("Lines", "A supply receipt holds no seed item"), (supplyWithSeed!.Details!["field"], supplyWithSeed.Details["hint"]));
        Assert.Equal("MSG-GEN-01", seedWithRope.Message.Code);
        Assert.Null(ReceiptKindRules.Mismatch(PurchaseReceiptKind.Seed, [InputItemGroup.Seed, null]));
        Assert.Null(ReceiptKindRules.Mismatch(PurchaseReceiptKind.Supply, [InputItemGroup.Material, InputItemGroup.Other]));
        Assert.Null(ReceiptKindRules.Mismatch(PurchaseReceiptKind.Seed, []));
    }

    [Fact]
    public void A_return_without_a_receipt_may_not_mix_seed_items_with_the_others()
    {
        Assert.True(ReceiptKindRules.MixesKinds([InputItemGroup.Seed, InputItemGroup.Other]));
        Assert.False(ReceiptKindRules.MixesKinds([InputItemGroup.Seed, InputItemGroup.Seed]));
        Assert.False(ReceiptKindRules.MixesKinds([InputItemGroup.Material, InputItemGroup.Other]));
        Assert.False(ReceiptKindRules.MixesKinds([]));
    }

    [Fact]
    public void An_undefined_kind_is_not_accepted()
    {
        Assert.True(ReceiptKindRules.IsDefined(null));
        Assert.True(ReceiptKindRules.IsDefined(PurchaseReceiptKind.Seed));
        Assert.False(ReceiptKindRules.IsDefined((PurchaseReceiptKind)7));
    }

    [Fact]
    public async Task A_new_receipt_that_starts_with_seed_and_adds_a_material_line_is_refused_on_Lines()
    {
        var (_, error) = await Validator().CheckDraftAsync(Request(null, seed, rope), storedKind: null, CancellationToken.None);

        Assert.Equal(("Lines", "A seed receipt holds only seed items"), (error!.Details!["field"], error.Details["hint"]));
    }

    [Fact]
    public async Task A_new_receipt_that_starts_with_material_and_adds_a_seed_line_is_refused_on_Lines()
    {
        var (_, error) = await Validator().CheckDraftAsync(Request(null, rope, fuel, seed), storedKind: null, CancellationToken.None);

        Assert.Equal(("Lines", "A supply receipt holds no seed item"), (error!.Details!["field"], error.Details["hint"]));
    }

    [Fact]
    public async Task The_kind_sent_on_a_new_receipt_decides_what_the_lines_must_be()
    {
        var seedKindWithRope = await Validator().CheckDraftAsync(Request(PurchaseReceiptKind.Seed, rope), storedKind: null, CancellationToken.None);
        var supplyKindWithSeed = await Validator().CheckDraftAsync(Request(PurchaseReceiptKind.Supply, seed), storedKind: null, CancellationToken.None);

        Assert.Equal("A seed receipt holds only seed items", seedKindWithRope.Error!.Details!["hint"]);
        Assert.Equal("A supply receipt holds no seed item", supplyKindWithSeed.Error!.Details!["hint"]);
    }

    [Fact]
    public async Task A_receipt_of_one_kind_is_accepted_and_so_is_a_Draft_with_no_item_yet()
    {
        Assert.Null((await Validator().CheckDraftAsync(Request(null, seed, seed), storedKind: null, CancellationToken.None)).Error);
        Assert.Null((await Validator().CheckDraftAsync(Request(null, rope, fuel), storedKind: null, CancellationToken.None)).Error);
        Assert.Null((await Validator().CheckDraftAsync(Request(PurchaseReceiptKind.Seed), storedKind: null, CancellationToken.None)).Error);
        var noItem = new SavePurchaseReceiptRequest(supplier.Id, null, null, [new ReceiptLineInput(null, null, null)]);
        Assert.Null((await Validator().CheckDraftAsync(noItem, PurchaseReceiptKind.Seed, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task A_saved_Draft_keeps_its_kind_the_request_cannot_change_it_and_the_lines_must_match_it()
    {
        var change = await Validator().CheckDraftAsync(Request(PurchaseReceiptKind.Supply, rope), PurchaseReceiptKind.Seed, CancellationToken.None);
        var sameKind = await Validator().CheckDraftAsync(Request(PurchaseReceiptKind.Seed, seed), PurchaseReceiptKind.Seed, CancellationToken.None);
        var materialOnSeedDraft = await Validator().CheckDraftAsync(Request(null, rope), PurchaseReceiptKind.Seed, CancellationToken.None);
        var seedOnSupplyDraft = await Validator().CheckDraftAsync(Request(null, seed), PurchaseReceiptKind.Supply, CancellationToken.None);

        Assert.Equal("Kind", change.Error!.Details!["field"]);
        Assert.Null(sameKind.Error);
        Assert.Equal("A seed receipt holds only seed items", materialOnSeedDraft.Error!.Details!["hint"]);
        Assert.Equal("A supply receipt holds no seed item", seedOnSupplyDraft.Error!.Details!["hint"]);
    }

    [Fact]
    public async Task Completing_checks_the_lines_against_the_kind_of_the_receipt()
    {
        var receipt = new PurchaseReceipt
        {
            Kind = PurchaseReceiptKind.Seed,
            SupplierId = supplier.Id,
            ReceiptDate = new DateOnly(2026, 11, 17),
            Lines =
            [
                new PurchaseReceiptLine { LineNumber = 1, ItemId = seed.Id, EnteredQuantity = 5, EnteredUnitPrice = 100, Quantity = 5, UnitPrice = 100, LineAmount = 500 },
                new PurchaseReceiptLine { LineNumber = 2, ItemId = rope.Id, EnteredQuantity = 5, EnteredUnitPrice = 100, Quantity = 5, UnitPrice = 100, LineAmount = 500 },
            ],
        };
        var validator = Validator();
        var refs = await validator.LoadAsync(receipt.Lines.Select(l => l.ItemId), [], CancellationToken.None);

        var refused = await validator.CheckCompleteAsync(receipt, refs, CancellationToken.None);
        receipt.Lines.Remove(receipt.Lines.Last());
        var accepted = await validator.CheckCompleteAsync(receipt, refs, CancellationToken.None);

        Assert.Equal("A seed receipt holds only seed items", refused!.Details!["hint"]);
        Assert.Null(accepted);
    }
}
