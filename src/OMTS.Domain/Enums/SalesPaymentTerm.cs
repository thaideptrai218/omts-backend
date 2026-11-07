namespace OMTS.Domain.Enums;

/// <summary>The payment term of a sales invoice (BR-SALE-07): Pay later puts the total, less any cash part, on the customer debt. Never changed after the save.</summary>
public enum SalesPaymentTerm
{
    PayNow,
    PayLater,
}
