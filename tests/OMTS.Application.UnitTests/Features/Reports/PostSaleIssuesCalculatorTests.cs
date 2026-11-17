using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Reports.PostSaleIssues;
using OMTS.Domain.Constants;

namespace OMTS.Application.UnitTests.Features.Reports;

/// <summary>BR-REPORT-03 as a pure function: each grouping, the lot grouping that counts only returns, and the events of a group.</summary>
public class PostSaleIssuesCalculatorTests
{
    private static readonly Guid ZoneA = Guid.NewGuid();
    private static readonly Guid ZoneB = Guid.NewGuid();
    private static readonly Guid Buyer = Guid.NewGuid();
    private static readonly Guid LotOne = Guid.NewGuid();
    private static readonly Guid LotTwo = Guid.NewGuid();
    private static readonly Guid InvoiceA = Guid.NewGuid();
    private static readonly Guid InvoiceB = Guid.NewGuid();
    private static readonly Guid InvoiceC = Guid.NewGuid();
    private static readonly Guid ReturnA = Guid.NewGuid();

    private static PostSaleEvent Event(PostSaleEventType type, Guid id, DateOnly date, Guid invoice, string code, Guid zone, string zoneName, Guid? customer, decimal? amount, int? status = null) =>
        new(type, id, date, invoice, code, zone, zoneName, customer, customer is null ? null : "Buyer", "note", amount, status);

    // Invoice A (zone A, Buyer, sells lots 1 and 2): two claims (Awaiting, Accepted) and a return of 300,000 (10 kg back for sale from lot 1, 5 kg thrown away from lot 2).
    // Invoice B (zone B, Buyer, sells lot 1): a Rejected claim and a price cut of 20,000. Invoice C (zone A, walk-in, sells lot 2): cancelled, total 60,000.
    private static readonly PostSaleFacts Facts = new(
        [
            Event(PostSaleEventType.Claim, Guid.NewGuid(), new DateOnly(2026, 11, 3), InvoiceA, "HD000001", ZoneA, "Zone A", Buyer, null, StatusIds.QualityClaim.AwaitingResponse),
            Event(PostSaleEventType.Claim, Guid.NewGuid(), new DateOnly(2026, 11, 4), InvoiceA, "HD000001", ZoneA, "Zone A", Buyer, null, StatusIds.QualityClaim.Accepted),
            Event(PostSaleEventType.Return, ReturnA, new DateOnly(2026, 11, 6), InvoiceA, "HD000001", ZoneA, "Zone A", Buyer, 300_000m),
            Event(PostSaleEventType.Claim, Guid.NewGuid(), new DateOnly(2026, 11, 5), InvoiceB, "HD000002", ZoneB, "Zone B", Buyer, null, StatusIds.QualityClaim.Rejected),
            Event(PostSaleEventType.PriceCut, Guid.NewGuid(), new DateOnly(2026, 11, 7), InvoiceB, "HD000002", ZoneB, "Zone B", Buyer, 20_000m),
            Event(PostSaleEventType.Cancellation, InvoiceC, new DateOnly(2026, 11, 8), InvoiceC, "HD000003", ZoneA, "Zone A", null, 60_000m),
        ],
        [new(InvoiceA, LotOne, "LOT-1"), new(InvoiceA, LotTwo, "LOT-2"), new(InvoiceB, LotOne, "LOT-1"), new(InvoiceC, LotTwo, "LOT-2")],
        [new(ReturnA, LotOne, "LOT-1", 10m, 0m), new(ReturnA, LotTwo, "LOT-2", 5m, 5m)]);

    [Fact]
    public void The_totals_count_every_event_once_whatever_the_grouping()
    {
        foreach (var grouping in Enum.GetValues<PostSaleGrouping>())
        {
            var t = PostSaleIssuesCalculator.Build(Facts, grouping).Totals;

            Assert.Equal((3, 1, 1, 1), (t.ClaimsSent, t.ClaimsAwaiting, t.ClaimsRejected, t.ClaimsAccepted));
            Assert.Equal((1, 15m, 5m, 300_000m), (t.ReturnsCount, t.ReturnedKg, t.DiscardedKg, t.ReturnsValue));
            Assert.Equal((1, 20_000m, 1, 60_000m), (t.PriceCutsCount, t.PriceCutsAmount, t.CancelledCount, t.CancelledTotal));
        }
    }

    [Fact]
    public void Grouped_by_customer_the_walk_in_invoices_have_a_row_of_their_own()
    {
        var groups = PostSaleIssuesCalculator.Build(Facts, PostSaleGrouping.Customer).Groups;

        var buyer = groups.Single(g => g.Key == Buyer.ToString());
        var walkIn = groups.Single(g => g.Key == PostSaleIssuesCalculator.WalkInKey);
        Assert.Equal((3, 1, 1, 1, 1, 15m, 5m, 300_000m, 1, 20_000m, 0, 0m),
            (buyer.ClaimsSent, buyer.ClaimsAwaiting, buyer.ClaimsRejected, buyer.ClaimsAccepted, buyer.ReturnsCount, buyer.ReturnedKg, buyer.DiscardedKg, buyer.ReturnsValue, buyer.PriceCutsCount,
                buyer.PriceCutsAmount, buyer.CancelledCount, buyer.CancelledTotal));
        Assert.Equal((0, 1, 60_000m), (walkIn.ClaimsSent, walkIn.CancelledCount, walkIn.CancelledTotal));
    }

    [Fact]
    public void Grouped_by_zone_each_event_is_in_the_zone_of_its_invoice()
    {
        var groups = PostSaleIssuesCalculator.Build(Facts, PostSaleGrouping.Zone).Groups;

        var a = groups.Single(g => g.Key == ZoneA.ToString());
        var b = groups.Single(g => g.Key == ZoneB.ToString());
        Assert.Equal((2, 1, 300_000m, 0, 1), (a.ClaimsSent, a.ReturnsCount, a.ReturnsValue, a.PriceCutsCount, a.CancelledCount));
        Assert.Equal((1, 0, 1, 20_000m), (b.ClaimsSent, b.ReturnsCount, b.PriceCutsCount, b.PriceCutsAmount));
    }

    // Lot 1 is sold by invoices A and B, lot 2 by A and C. The return takes back both lots: once for each, with the kg of that lot. Claims, price cuts and cancellations count for
    // every lot their invoice sells; the amounts that apply to a whole invoice are not split: null.
    [Fact]
    public void Grouped_by_lot_the_issues_of_the_invoices_of_the_lot_count_with_the_kg_of_the_lot_and_no_amount_is_split()
    {
        var groups = PostSaleIssuesCalculator.Build(Facts, PostSaleGrouping.Lot).Groups;

        Assert.Equal(["LOT-1", "LOT-2"], groups.Select(g => g.Label));
        var (one, two) = (groups[0], groups[1]);
        Assert.Equal((3, 1, 1, 1, 1, 10m, 0m, 1, 0), (one.ClaimsSent, one.ClaimsAwaiting, one.ClaimsRejected, one.ClaimsAccepted, one.ReturnsCount, one.ReturnedKg, one.DiscardedKg, one.PriceCutsCount, one.CancelledCount));
        Assert.Equal((2, 1, 0, 1, 1, 5m, 5m, 0, 1), (two.ClaimsSent, two.ClaimsAwaiting, two.ClaimsRejected, two.ClaimsAccepted, two.ReturnsCount, two.ReturnedKg, two.DiscardedKg, two.PriceCutsCount, two.CancelledCount));
        foreach (var group in groups)
        {
            Assert.Null(group.ReturnsValue);
            Assert.Null(group.PriceCutsAmount);
            Assert.Null(group.CancelledTotal);
        }
    }

    [Fact]
    public void A_lot_with_only_a_claim_has_a_row()
    {
        var claimOnly = new PostSaleFacts(
            [Event(PostSaleEventType.Claim, Guid.NewGuid(), new DateOnly(2026, 11, 3), InvoiceB, "HD000002", ZoneB, "Zone B", Buyer, null, StatusIds.QualityClaim.AwaitingResponse)],
            [new(InvoiceB, LotOne, "LOT-1")],
            []);

        var group = Assert.Single(PostSaleIssuesCalculator.Build(claimOnly, PostSaleGrouping.Lot).Groups);
        Assert.Equal(("LOT-1", 1, 0), (group.Label, group.ClaimsSent, group.ReturnsCount));
    }

    [Fact]
    public void The_events_of_a_customer_or_a_zone_are_newest_first_with_their_type_status_and_amount()
    {
        var items = PostSaleIssuesCalculator.ItemsOf(Facts, PostSaleGrouping.Customer, Buyer.ToString());

        Assert.Equal(
            ["priceCut", "return", "claim", "claim", "claim"], items.Select(i => i.Type));
        Assert.Equal([20_000m, 300_000m, null, null, null], items.Select(i => i.Amount));
        Assert.Equal(["rejected", "accepted", "awaitingResponse"], items.Where(i => i.Type == "claim").Select(i => i.Status));
        Assert.Equal(["cancellation"], PostSaleIssuesCalculator.ItemsOf(Facts, PostSaleGrouping.Customer, PostSaleIssuesCalculator.WalkInKey).Select(i => i.Type));
        Assert.Equal(4, PostSaleIssuesCalculator.ItemsOf(Facts, PostSaleGrouping.Zone, ZoneA.ToString()).Count);
        Assert.Empty(PostSaleIssuesCalculator.ItemsOf(Facts, PostSaleGrouping.Zone, Guid.NewGuid().ToString()));
    }

    [Fact]
    public void The_events_of_a_lot_are_the_issues_of_the_invoices_that_sell_it()
    {
        var one = PostSaleIssuesCalculator.ItemsOf(Facts, PostSaleGrouping.Lot, LotOne.ToString());
        var two = PostSaleIssuesCalculator.ItemsOf(Facts, PostSaleGrouping.Lot, LotTwo.ToString());

        // Lot 1 is on invoices A and B; lot 2 on invoices A and C.
        Assert.Equal(5, one.Count);
        Assert.Equal(["HD000003", "HD000001", "HD000001", "HD000001"], two.Select(i => i.InvoiceCode));
        Assert.Contains(two, i => i.Type == "cancellation" && i.Amount == 60_000m);
    }

    [Fact]
    public void No_event_gives_no_group_and_zero_totals()
    {
        var empty = PostSaleIssuesCalculator.Build(new PostSaleFacts([], [], []), PostSaleGrouping.Customer);

        Assert.Empty(empty.Groups);
        Assert.Equal((0, 0m, 0, 0m), (empty.Totals.ClaimsSent, empty.Totals.ReturnsValue, empty.Totals.CancelledCount, empty.Totals.CancelledTotal));
    }
}
