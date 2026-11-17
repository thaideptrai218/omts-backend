namespace OMTS.Application.Features.Purchasing.SeedLots;

/// <summary>
/// Where a seed lot (a Seed line of a Completed receipt) stands. It is worked out from the quantities every time and never stored. The last four are final: nothing of the lot is left.
/// </summary>
public enum SeedLotState
{
    /// <summary>Nothing has left the lot but returns (or nothing at all).</summary>
    Unused,

    /// <summary>Pieces are in the nursery (a batch still Nursing, or pieces left in the pool of a Completed batch).</summary>
    Nursing,

    /// <summary>Some of the lot is seeded, written off or issued, and some is left.</summary>
    PartlySeeded,

    /// <summary>Final: everything that left the lot was hung on lanes (nursery loss aside).</summary>
    FullySeeded,

    /// <summary>Final: the whole lot went back to the supplier.</summary>
    FullyReturned,

    /// <summary>Final: the whole lot was lost in the nursery or written off, none of it hung.</summary>
    FullyLost,

    /// <summary>Final: the lot is used up by a mix of seeding, returns, losses and issues.</summary>
    UsedUp,
}

/// <summary>
/// The raw quantities of one seed lot, all in the base unit of the seed item.
/// <see cref="NurseryIn"/> is what every batch that is not Cancelled took out of the stock; <see cref="NurseryLoss"/> what was lost in those that are Completed or Closed (the pieces that did not survive plus
/// what the close wrote off); <see cref="SeededViaNursery"/> what the cycles hung from the pool of those batches; <see cref="SeededDirect"/> what was hung without a nursery; <see cref="IssuedOther"/> what manual
/// issues and disposals (Completed) took from the lot.
/// </summary>
public sealed record SeedLotFigures(
    decimal Received,
    decimal Returned,
    decimal NurseryIn,
    decimal NurseryLoss,
    decimal SeededViaNursery,
    decimal SeededDirect,
    decimal IssuedOther,
    DateTimeOffset? LastUsedAt = null)
{
    /// <summary>Everything hung on lanes, directly or from the nursery.</summary>
    public decimal Seeded => SeededDirect + SeededViaNursery;

    /// <summary>What the nursery still holds: the batches Nursing and the pieces left in the pool of Completed ones.</summary>
    public decimal Nursing => Math.Max(0m, NurseryIn - NurseryLoss - SeededViaNursery);

    /// <summary>What is left of the lot, in the nursery or in the stock; 0 once the lot is used up. The seeding is not compared with the lot (GBR-04), so it never goes below 0.</summary>
    public decimal Remaining => Math.Max(0m, Received - Returned - Seeded - NurseryLoss - IssuedOther);

    /// <summary>What is still in the stock and can be put in the nursery, seeded or returned: the remaining quantity less what the nursery holds.</summary>
    public decimal Available => Math.Max(0m, Remaining - Nursing);

    public SeedLotState State => SeedLotStates.Derive(this);
}

/// <summary>
/// The state of a seed lot from its quantities. While something is left: Nursing when the nursery holds pieces, PartlySeeded when some of the lot was seeded, lost or issued, else Unused. Once nothing is left:
/// FullySeeded when the lot was seeded and none of it returned or issued (a loss in the nursery is the price of that path), FullyReturned when only returned, FullyLost when only lost or issued, and UsedUp for any mix.
/// </summary>
public static class SeedLotStates
{
    public static SeedLotState Derive(SeedLotFigures f)
    {
        var lostOrIssued = f.NurseryLoss > 0m || f.IssuedOther > 0m;
        if (f.Remaining > 0m)
        {
            if (f.Nursing > 0m)
            {
                return SeedLotState.Nursing;
            }

            return f.Seeded > 0m || lostOrIssued ? SeedLotState.PartlySeeded : SeedLotState.Unused;
        }

        if (f.Received <= 0m && f.Seeded <= 0m && f.Returned <= 0m && !lostOrIssued)
        {
            // A lot with no quantity has nothing to use.
            return SeedLotState.Unused;
        }

        if (f.Seeded > 0m)
        {
            return f.Returned <= 0m && f.IssuedOther <= 0m ? SeedLotState.FullySeeded : SeedLotState.UsedUp;
        }

        if (f.Returned > 0m)
        {
            return lostOrIssued ? SeedLotState.UsedUp : SeedLotState.FullyReturned;
        }

        return SeedLotState.FullyLost;
    }

    public static bool IsFinal(SeedLotState state) => state is SeedLotState.FullySeeded or SeedLotState.FullyReturned or SeedLotState.FullyLost or SeedLotState.UsedUp;

    /// <summary>The state a Seed receipt shows for the lots it holds: the one state all its lots share; else Nursing when a lot is in the nursery, else PartlySeeded while any lot is not final, else UsedUp (every lot is final, in different ways).</summary>
    public static SeedLotState Combine(IReadOnlyCollection<SeedLotState> states)
    {
        if (states.Count == 0)
        {
            return SeedLotState.Unused;
        }

        if (states.Distinct().Count() == 1)
        {
            return states.First();
        }

        var open = states.Where(s => !IsFinal(s)).ToList();
        if (open.Count == 0)
        {
            return SeedLotState.UsedUp;
        }

        // Something is still open: Nursing wins when a lot is in the nursery; otherwise the receipt is partly used (a lot already finished, or one partly seeded).
        return open.Contains(SeedLotState.Nursing) ? SeedLotState.Nursing : SeedLotState.PartlySeeded;
    }
}
