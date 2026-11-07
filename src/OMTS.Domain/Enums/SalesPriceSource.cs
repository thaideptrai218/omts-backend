namespace OMTS.Domain.Enums;

/// <summary>Where the unit price of an invoice line comes from (BR-SALE-06): the price list, or a price the Cooperative Owner decided.</summary>
public enum SalesPriceSource
{
    PriceList,
    DecidedByOwner,
}
