using System.Reflection;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Purchasing;

/// <summary>Stands in for an interface of which a test needs only a few members: the others throw, so a test never depends on one it did not set up.</summary>
public class StubProxy : DispatchProxy
{
    public Dictionary<string, Func<object?[], object?>> Handlers { get; } = [];

    public static T Create<T>(Action<Dictionary<string, Func<object?[], object?>>> setup)
        where T : class
    {
        var proxy = Create<T, StubProxy>();
        setup(((StubProxy)(object)proxy).Handlers);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (!Handlers.TryGetValue(targetMethod!.Name, out var handler))
        {
            throw new NotSupportedException($"{targetMethod.DeclaringType!.Name}.{targetMethod.Name} is not set up.");
        }

        var result = handler(args ?? []);
        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [result])
            : result;
    }
}

/// <summary>The movement ledger kept in memory; rows written are visible to the next request, like rows saved.</summary>
public sealed class InMemoryStock : IInputStockRepository
{
    public List<InputStockMovement> Rows { get; } = [];

    public Task<IReadOnlyDictionary<Guid, decimal>> OnHandAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, decimal>>(itemIds.ToDictionary(id => id, id => Rows.Where(r => r.ItemId == id).Sum(r => r.Quantity)));

    public Task<IReadOnlyList<InputStockMovement>> UnreversedOfDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InputStockMovement>>(Rows
            .Where(r => r.DocumentId == documentId && r.Kind != InputStockMovementKind.Reversal && !Rows.Any(x => x.ReversesMovementId == r.Id)).OrderBy(r => r.Sequence).ToList());

    public Task<bool> HasOnlyOpeningAsync(Guid itemId, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Where(r => r.ItemId == itemId).All(r => r.Kind is InputStockMovementKind.Opening or InputStockMovementKind.Reversal));

    public Task<decimal?> OpeningQuantityAsync(Guid itemId, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Where(r => r.ItemId == itemId && r.Kind == InputStockMovementKind.Opening).Select(r => (decimal?)r.Quantity).FirstOrDefault());

    public Task<IReadOnlyDictionary<(Guid ItemId, Guid? LotId), decimal>> BucketBalancesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<(Guid ItemId, Guid? LotId), decimal>>(
            Rows.Where(r => itemIds.Contains(r.ItemId)).GroupBy(r => (r.ItemId, r.SeedLotLineId)).ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity)));

    public Task<IReadOnlyDictionary<Guid, string>> LotCodesAsync(IReadOnlyCollection<Guid> lotLineIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(lotLineIds.ToDictionary(id => id, id => $"PN000001-{id.ToString()[..4]}"));

    public void Add(InputStockMovement movement)
    {
        movement.Sequence = Rows.Count + 1;
        Rows.Add(movement);
    }
}

/// <summary>The ledger writes of issues: what leaves the stock, the check for a negative stock per item and per seed lot, and the undoing of an edited or cancelled issue.</summary>
public sealed class InputStockLedgerIssueTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.FromHours(7));

    private sealed class Account : ICurrentUser
    {
        public Guid? AccountId => null;
        public Guid? SessionId => null;
    }

    private sealed class Fixture
    {
        public InMemoryStock Stock { get; } = new();
        public FarmingInputItem Seed { get; } = new() { Code = "GI000001", Name = "Hàu giống", Group = InputItemGroup.Seed, Unit = "con", CostPrice = 100m };
        public FarmingInputItem Rope { get; } = new() { Code = "VT000001", Name = "Dây", Group = InputItemGroup.Material, Unit = "m", CostPrice = 50m };
        public Guid LotA { get; } = Guid.NewGuid();
        public Guid LotB { get; } = Guid.NewGuid();
        public bool AllowNegative { get; set; }
        public InputCostMethod Method { get; set; } = InputCostMethod.Fixed;

        /// <summary>A new ledger as a request gets it: it reads the stock as saved so far.</summary>
        public InputStockLedger Ledger() => new(
            Stock,
            StubProxy.Create<IFarmingInputItemRepository>(h => h["GetByIdsForUpdateAsync"] = a => new List<FarmingInputItem> { Seed, Rope }.Where(i => ((IReadOnlyCollection<Guid>)a[0]!).Contains(i.Id)).ToList()),
            StubProxy.Create<ICooperativeRepository>(h => h["GetCurrentAsync"] = _ => new Cooperative { AllowNegativeInputQuantity = AllowNegative, InputCostMethod = Method }),
            StubProxy.Create<ISupplierRepository>(_ => { }),
            new Account(),
            TimeProvider.System);

        public void Stocked(FarmingInputItem item, Guid? lot, decimal quantity, InputStockMovementKind kind = InputStockMovementKind.Receipt) =>
            Stock.Add(new InputStockMovement { ItemId = item.Id, SeedLotLineId = lot, Kind = kind, DocumentId = Guid.NewGuid(), DocumentCode = "X", OccurredAt = At, Quantity = quantity });
    }

    private static Task IssueAsync(InputStockLedger ledger, Guid document, params StockPosting[] postings) =>
        ledger.PostIssueAsync(InputStockMovementKind.Issue, document, "XD000001", At, "KV01 · B01", postings, CancellationToken.None);

    [Fact]
    public async Task An_issue_takes_its_quantity_out_of_the_item_at_the_cost_it_has()
    {
        var f = new Fixture();
        f.Stocked(f.Rope, null, 20m);
        var ledger = f.Ledger();

        await IssueAsync(ledger, Guid.NewGuid(), new StockPosting(f.Rope.Id, null, -8m, null));

        Assert.Null(await ledger.CheckNotNegativeAsync(CancellationToken.None));
        Assert.Equal(12m, (await ledger.OnHandAsync([f.Rope.Id], CancellationToken.None))[f.Rope.Id]);
        var row = f.Stock.Rows.Last();
        Assert.Equal((InputStockMovementKind.Issue, -8m, 50m, "KV01 · B01", (decimal?)null), (row.Kind, row.Quantity, row.UnitCost!.Value, row.PartyName, row.TransactionPrice));
    }

    [Fact]
    public async Task An_issue_below_zero_is_refused_when_negative_stock_is_not_allowed_and_accepted_when_it_is()
    {
        var f = new Fixture();
        f.Stocked(f.Rope, null, 5m);

        var strict = f.Ledger();
        await IssueAsync(strict, Guid.NewGuid(), new StockPosting(f.Rope.Id, null, -7m, null));
        var refused = await strict.CheckNotNegativeAsync(CancellationToken.None);

        Assert.NotNull(refused);
        Assert.Equal(ErrorKind.Conflict, refused!.Kind);
        Assert.Equal("MSG-INPUT-01", refused.Message.Code);
        Assert.Equal(-2m, refused.Details!["balanceAfter"]);

        f.AllowNegative = true;
        var lenient = f.Ledger();
        await IssueAsync(lenient, Guid.NewGuid(), new StockPosting(f.Rope.Id, null, -7m, null));
        Assert.Null(await lenient.CheckNotNegativeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_seed_lot_cannot_go_below_zero_even_when_the_item_has_enough_in_other_lots()
    {
        var f = new Fixture();
        f.Stocked(f.Seed, f.LotA, 10m);
        f.Stocked(f.Seed, f.LotB, 10m);
        var ledger = f.Ledger();

        await IssueAsync(ledger, Guid.NewGuid(), new StockPosting(f.Seed.Id, f.LotA, -12m, null));
        var refused = await ledger.CheckNotNegativeAsync(CancellationToken.None);

        Assert.Equal("MSG-INPUT-01", refused!.Message.Code);
        Assert.Equal(f.LotA, refused.Details!["seedLotLineId"]);
        Assert.Contains("lô PN000001-", refused.Message.Text);
    }

    [Fact]
    public async Task A_line_without_a_lot_draws_from_the_quantity_that_has_no_lot()
    {
        var f = new Fixture();
        f.Stocked(f.Seed, null, 5m, InputStockMovementKind.Opening);
        f.Stocked(f.Seed, f.LotA, 100m);

        var fits = f.Ledger();
        await IssueAsync(fits, Guid.NewGuid(), new StockPosting(f.Seed.Id, null, -5m, null));
        Assert.Null(await fits.CheckNotNegativeAsync(CancellationToken.None));

        // 6 do not fit in the 5 that have no lot, though the item holds 105.
        var g = new Fixture();
        g.Stocked(g.Seed, null, 5m, InputStockMovementKind.Opening);
        g.Stocked(g.Seed, g.LotA, 100m);
        var tooMuch = g.Ledger();
        await IssueAsync(tooMuch, Guid.NewGuid(), new StockPosting(g.Seed.Id, null, -6m, null));
        var refused = await tooMuch.CheckNotNegativeAsync(CancellationToken.None);
        Assert.Equal((null, -1m), (refused!.Details!["seedLotLineId"], refused.Details["balanceAfter"]));
    }

    [Fact]
    public async Task Several_lines_of_one_item_are_judged_together()
    {
        var f = new Fixture();
        f.Stocked(f.Rope, null, 10m);
        var ledger = f.Ledger();

        await IssueAsync(ledger, Guid.NewGuid(), new StockPosting(f.Rope.Id, null, -6m, null), new StockPosting(f.Rope.Id, null, -6m, null));

        Assert.NotNull(await ledger.CheckNotNegativeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Editing_an_issue_undoes_its_rows_and_is_judged_by_the_result()
    {
        var f = new Fixture();
        f.Stocked(f.Rope, null, 10m);
        var issue = Guid.NewGuid();
        var first = f.Ledger();
        await IssueAsync(first, issue, new StockPosting(f.Rope.Id, null, -10m, null));
        Assert.Null(await first.CheckNotNegativeAsync(CancellationToken.None));

        // The same 10 again would be 20 against a stock of 10 without the undo; with it the result is 0.
        var edit = f.Ledger();
        await edit.ReverseAsync(issue, afterReceiptRemoved: false, CancellationToken.None);
        await IssueAsync(edit, issue, new StockPosting(f.Rope.Id, null, -10m, null));
        Assert.Null(await edit.CheckNotNegativeAsync(CancellationToken.None));
        Assert.Equal(0m, (await edit.OnHandAsync([f.Rope.Id], CancellationToken.None))[f.Rope.Id]);

        var reversal = f.Stock.Rows.Single(r => r.Kind == InputStockMovementKind.Reversal);
        Assert.Equal((10m, At), (reversal.Quantity, reversal.OccurredAt));
        Assert.Equal(f.Stock.Rows.First(r => r.Kind == InputStockMovementKind.Issue).Id, reversal.ReversesMovementId);
    }

    [Fact]
    public async Task Cancelling_an_issue_puts_everything_back_in_the_lot_it_came_from()
    {
        var f = new Fixture();
        f.Stocked(f.Seed, f.LotA, 10m);
        var issue = Guid.NewGuid();
        var first = f.Ledger();
        await IssueAsync(first, issue, new StockPosting(f.Seed.Id, f.LotA, -4m, null));

        var cancel = f.Ledger();
        await cancel.ReverseAsync(issue, afterReceiptRemoved: false, CancellationToken.None);

        var balances = await f.Stock.BucketBalancesAsync([f.Seed.Id], CancellationToken.None);
        Assert.Equal(10m, balances[(f.Seed.Id, f.LotA)]);
        // A second undo finds nothing left to undo.
        var again = f.Ledger();
        await again.ReverseAsync(issue, afterReceiptRemoved: false, CancellationToken.None);
        Assert.Equal(10m, (await f.Stock.BucketBalancesAsync([f.Seed.Id], CancellationToken.None))[(f.Seed.Id, f.LotA)]);
    }

    [Fact]
    public async Task An_issue_leaves_the_average_cost_alone()
    {
        var f = new Fixture { Method = InputCostMethod.Average };
        f.Stocked(f.Rope, null, 10m);
        var ledger = f.Ledger();

        await IssueAsync(ledger, Guid.NewGuid(), new StockPosting(f.Rope.Id, null, -4m, 9_999m));

        Assert.Equal(50m, f.Rope.CostPrice);
    }

    [Fact]
    public async Task Only_an_issue_or_a_disposal_posts_through_the_issue_method()
    {
        var f = new Fixture();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            f.Ledger().PostIssueAsync(InputStockMovementKind.Receipt, Guid.NewGuid(), "PN1", At, null, [], CancellationToken.None));
    }
}

