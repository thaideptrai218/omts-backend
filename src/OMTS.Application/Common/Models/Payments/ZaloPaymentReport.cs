namespace OMTS.Application.Common.Models.Payments;

/// <summary>Zalo's answer to the report that the payment of an order arrived (<c>custom-callback-payment</c>).</summary>
/// <param name="Error">What Zalo or the network answered when the report failed; the report is sent again later.</param>
public sealed record ZaloPaymentReport(bool Accepted, string? Error)
{
    public static readonly ZaloPaymentReport Done = new(true, null);

    public static ZaloPaymentReport Failed(string error) => new(false, error);
}
