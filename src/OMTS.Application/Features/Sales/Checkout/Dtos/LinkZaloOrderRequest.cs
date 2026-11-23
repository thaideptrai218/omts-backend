namespace OMTS.Application.Features.Sales.Checkout.Dtos;

/// <summary>The Zalo order of the <c>PAY_BY_CUSTOM_METHOD</c> event: the trader confirmed the payment in Zalo and the Mini App now shows the QR code.</summary>
/// <param name="ZaloOrderId">The <c>orderId</c> of the event, 1 to 100 characters.</param>
public sealed record LinkZaloOrderRequest(string? ZaloOrderId);
