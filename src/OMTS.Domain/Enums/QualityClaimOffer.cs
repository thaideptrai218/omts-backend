namespace OMTS.Domain.Enums;

/// <summary>What the Cooperative Owner offers when accepting a quality claim (BR-CLAIM-02, BF-05.1 step 4).</summary>
public enum QualityClaimOffer
{
    /// <summary>A return of all the goods of the invoice, carried out by CLAIM-03 when the trader chooses it.</summary>
    ReturnAllGoods,

    /// <summary>A price cut of the whole invoice, carried out by SALE-07 when the trader chooses it.</summary>
    PriceCutWholeInvoice,
}
