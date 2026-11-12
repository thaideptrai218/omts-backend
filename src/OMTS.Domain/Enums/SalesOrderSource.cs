namespace OMTS.Domain.Enums;

/// <summary>How an order came to the cooperative ("Nguồn đặt"): the trader app, a phone call, or a buyer at the port. An order made from a purchase request is always <see cref="App"/>.</summary>
public enum SalesOrderSource
{
    App,
    Phone,
    AtPort,
}
