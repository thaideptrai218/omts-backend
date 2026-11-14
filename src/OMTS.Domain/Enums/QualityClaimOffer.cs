namespace OMTS.Domain.Enums;

/// <summary>What the Cooperative Owner proposes to the trader on a quality claim (BR-CLAIM-02, BF-05.1 step 4); the trader accepts or declines it (PM answer 12/11).</summary>
public enum QualityClaimOffer
{
    /// <summary>A return of all the goods of the invoice, carried out by CLAIM-03 when the trader chooses it.</summary>
    ReturnAllGoods,

    /// <summary>A price cut of the whole invoice for the amount of the offer (<c>QualityClaim.OfferAmount</c>), carried out by SALE-07 when the trader accepts it.</summary>
    PriceCutWholeInvoice,
}
