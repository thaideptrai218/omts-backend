namespace OMTS.Domain.Enums;

/// <summary>
/// What wrote a row of the product stock ledger: a harvest lot split per product (+, at its weighed kg), a sale on an invoice line (−), a disposal (−), a sales return (+, a later module), the reversal that
/// undoes a row, or the close of a lot, which takes what is left of a product on the lot off the stock (−; a reversal undoes it when the lot is reopened). New kinds go at the end.
/// </summary>
public enum ProductStockMovementKind
{
    Harvest,
    Sale,
    Disposal,
    Return,
    Reversal,
    LotClose,
}
