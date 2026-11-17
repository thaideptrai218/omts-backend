namespace OMTS.Domain.Enums;

/// <summary>What happens to the oysters of a sales return line, as the Cooperative Owner decided (BR-CLAIM-03, BF-05.1 step 6).</summary>
public enum SalesReturnOutcome
{
    /// <summary>The kg goes back into its harvest lot, and a Closed lot is Reopened.</summary>
    BackForSale,

    /// <summary>The kg is discarded kg: it counts in the lost kg of the lot, and the lot is no longer listed on the provenance page of the invoice.</summary>
    ThrownAway,
}
