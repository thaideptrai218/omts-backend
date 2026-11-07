using System.Globalization;

namespace OMTS.Application.Common.Messages;

/// <summary>What keeps a harvest lot from being published (TRACE-01).</summary>
public enum OriginGapKind
{
    /// <summary>A lane of the lot has no seeding session in the lot's cycle (BR-TRACE-01).</summary>
    NoSeedingSession,

    /// <summary>The seed lots hung on a lane of the lot are all on purchase receipts that are not Completed (BR-TRACE-01, GBR-04).</summary>
    SeedLotNotOnCompletedReceipt,

    /// <summary>At the end of a sale: the caller does not publish in the lot's zone, which may differ from the invoice zone (BR-SALE-08).</summary>
    NotPublisherOfZone,
}

/// <summary>
/// One item of the <c>missing_items</c> of MSG-TRACE-01, as atomic values (kind, lane number, seed lot codes, zone code) so a client renders it in its own language. The system names
/// what is missing and never fills it in (BR-TRACE-03).
/// </summary>
/// <param name="LaneNumber">The lane, null for <see cref="OriginGapKind.NotPublisherOfZone"/>.</param>
/// <param name="SeedLotCodes">The seed lots of the lane that are not on a Completed receipt; empty for the other kinds.</param>
/// <param name="ZoneCode">The lot's zone for <see cref="OriginGapKind.NotPublisherOfZone"/>, else null.</param>
public sealed record OriginGap(OriginGapKind Kind, int? LaneNumber, IReadOnlyList<string> SeedLotCodes, string? ZoneCode)
{
    public static OriginGap NoSeedingSession(int laneNumber) => new(OriginGapKind.NoSeedingSession, laneNumber, [], null);

    public static OriginGap SeedLotNotOnCompletedReceipt(int laneNumber, IReadOnlyList<string> seedLotCodes) =>
        new(OriginGapKind.SeedLotNotOnCompletedReceipt, laneNumber, seedLotCodes, null);

    public static OriginGap NotPublisherOfZone(string zoneCode) => new(OriginGapKind.NotPublisherOfZone, null, [], zoneCode);

    /// <summary>The English phrase of {missing_items}, e.g. "lane 2 has no seeding session in the cycle of the lot".</summary>
    public string Describe()
    {
        var lane = LaneNumber?.ToString(CultureInfo.InvariantCulture);
        return Kind switch
        {
            OriginGapKind.NoSeedingSession => $"lane {lane} has no seeding session in the cycle of the lot",
            OriginGapKind.SeedLotNotOnCompletedReceipt => $"the seed lot {string.Join(", ", SeedLotCodes)} of lane {lane} is not on a completed purchase receipt",
            _ => $"only a Zone Manager of zone {ZoneCode} or the Cooperative Owner can publish it",
        };
    }
}
