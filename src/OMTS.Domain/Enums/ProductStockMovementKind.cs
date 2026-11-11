namespace OMTS.Domain.Enums;

/// <summary>What wrote a row of the product stock ledger: a harvest lot split per product (+), a sale on an invoice line (−), a disposal (−), a sales return (+, a later module) or the reversal that undoes a row.</summary>
public enum ProductStockMovementKind
{
    Harvest,
    Sale,
    Disposal,
    Return,
    Reversal,
}
