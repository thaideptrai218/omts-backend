namespace OMTS.Application.Common.Models.Payments;

/// <summary>
/// The parameters of <c>CheckoutSDK.createOrder</c>, signed by the server with the Checkout private key, which never leaves it. The Mini App passes them on unchanged: a
/// changed value no longer matches <see cref="Mac"/> and Zalo refuses the order.
/// </summary>
/// <param name="Amount">Whole VND.</param>
/// <param name="Desc">What Zalo shows the trader.</param>
/// <param name="Extradata">A JSON text Zalo gives back with the order.</param>
/// <param name="Method">A JSON text naming the custom payment method (<c>isCustom</c> true).</param>
/// <param name="Mac">HMAC-SHA256 (hex) of <c>amount, desc, extradata, item, method</c> as <c>key=value</c> joined by <c>&amp;</c>, keys in order, the item as JSON.</param>
public sealed record ZaloCheckoutOrder(long Amount, string Desc, IReadOnlyList<ZaloCheckoutItem> Item, string Extradata, string Method, string Mac);
