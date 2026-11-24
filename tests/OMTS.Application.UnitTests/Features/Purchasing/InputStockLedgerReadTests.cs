using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>How often the ledger of an item is read: the quantity on hand of an item and its seed lot buckets come from one read, once per item per request.</summary>
public sealed class InputStockLedgerReadTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.FromHours(7));

    private sealed class Account : ICurrentUser
    {
        public Guid? AccountId => null;
        public Guid? SessionId => null;
    }

    /// <summary>The in-memory ledger that counts the reads that sum it.</summary>
    private sealed class CountingStock(InMemoryStock inner) : IInputStockRepository
    {
        public int OnHandReads { get; private set; }
        public int BucketReads { get; private set; }

        public Task<IReadOnlyDictionary<Guid, decimal>> OnHandAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
        {
            OnHandReads++;
            return inner.OnHandAsync(itemIds, cancellationToken);
        }

        public Task<IReadOnlyDictionary<(Guid ItemId, Guid? LotId), decimal>> BucketBalancesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
        {
            BucketReads++;
            return inner.BucketBalancesAsync(itemIds, cancellationToken);
        }

        public Task<IReadOnlyList<InputStockMovement>> UnreversedOfDocumentAsync(Guid documentId, CancellationToken cancellationToken) => inner.UnreversedOfDocumentAsync(documentId, cancellationToken);
        public Task<bool> HasOnlyOpeningAsync(Guid itemId, CancellationToken cancellationToken) => inner.HasOnlyOpeningAsync(itemId, cancellationToken);
        public Task<decimal?> OpeningQuantityAsync(Guid itemId, CancellationToken cancellationToken) => inner.OpeningQuantityAsync(itemId, cancellationToken);
        public Task<IReadOnlyDictionary<Guid, string>> LotCodesAsync(IReadOnlyCollection<Guid> lotLineIds, CancellationToken cancellationToken) => inner.LotCodesAsync(lotLineIds, cancellationToken);
        public void Add(InputStockMovement movement) => inner.Add(movement);
    }

    private static (InputStockLedger Ledger, CountingStock Stock, FarmingInputItem Seed, FarmingInputItem Rope, Guid Lot) Setup()
    {
        var memory = new InMemoryStock();
        var seed = new FarmingInputItem { Code = "GI000001", Name = "Hàu giống", Group = InputItemGroup.Seed, Unit = "con", CostPrice = 100m };
        var rope = new FarmingInputItem { Code = "VT000001", Name = "Dây", Group = InputItemGroup.Material, Unit = "m", CostPrice = 50m };
        var lot = Guid.NewGuid();
        memory.Add(new InputStockMovement { ItemId = seed.Id, SeedLotLineId = lot, Kind = InputStockMovementKind.Receipt, DocumentId = Guid.NewGuid(), DocumentCode = "X", OccurredAt = At, Quantity = 70m });
        memory.Add(new InputStockMovement { ItemId = seed.Id, SeedLotLineId = null, Kind = InputStockMovementKind.Opening, DocumentId = seed.Id, DocumentCode = "X", OccurredAt = At, Quantity = 5m });
        memory.Add(new InputStockMovement { ItemId = rope.Id, SeedLotLineId = null, Kind = InputStockMovementKind.Receipt, DocumentId = Guid.NewGuid(), DocumentCode = "X", OccurredAt = At, Quantity = 20m });
        var stock = new CountingStock(memory);
        var ledger = new InputStockLedger(
            stock,
            StubProxy.Create<IFarmingInputItemRepository>(h => h["GetByIdsForUpdateAsync"] = a => new List<FarmingInputItem> { seed, rope }.Where(i => ((IReadOnlyCollection<Guid>)a[0]!).Contains(i.Id)).ToList()),
            StubProxy.Create<ICooperativeRepository>(h => h["GetCurrentAsync"] = _ => new Cooperative { AllowNegativeInputQuantity = true, InputCostMethod = InputCostMethod.Fixed }),
            StubProxy.Create<ISupplierRepository>(_ => { }),
            new Account(),
            TimeProvider.System);
        return (ledger, stock, seed, rope, lot);
    }

    [Fact]
    public async Task The_quantity_of_every_item_and_lot_comes_from_one_read_of_the_ledger()
    {
        var (ledger, stock, seed, rope, lot) = Setup();

        var onHand = await ledger.OnHandAsync([seed.Id, rope.Id], CancellationToken.None);

        Assert.Equal((75m, 20m), (onHand[seed.Id], onHand[rope.Id]));
        Assert.Equal((0, 1), (stock.OnHandReads, stock.BucketReads));
        await ledger.PostIssueAsync(
            InputStockMovementKind.Issue, Guid.NewGuid(), "XD000001", At, null, [new StockPosting(seed.Id, lot, -10m, null)], CancellationToken.None);
        Assert.Equal(65m, (await ledger.OnHandAsync([seed.Id], CancellationToken.None))[seed.Id]);
        Assert.Equal((0, 1), (stock.OnHandReads, stock.BucketReads));
    }

    [Fact]
    public async Task An_item_with_no_row_is_on_hand_zero_and_is_read_again_by_no_later_call()
    {
        var (ledger, stock, _, _, _) = Setup();
        var unknown = Guid.NewGuid();

        var first = await ledger.OnHandAsync([unknown], CancellationToken.None);
        var second = await ledger.OnHandAsync([unknown], CancellationToken.None);

        Assert.Equal((0m, 0m), (first[unknown], second[unknown]));
        Assert.Equal(1, stock.BucketReads);
    }
}
