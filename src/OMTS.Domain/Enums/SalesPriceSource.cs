namespace OMTS.Domain.Enums;

/// <summary>Where the unit price of an invoice line comes from (BR-SALE-06): the price list, a price the Cooperative Owner decided, or the price of the order line the invoice delivers.</summary>
public enum SalesPriceSource
{
    PriceList,
    DecidedByOwner,
    OrderPrice,
}
