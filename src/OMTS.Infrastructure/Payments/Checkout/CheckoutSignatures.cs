using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OMTS.Application.Common.Models.Payments;

namespace OMTS.Infrastructure.Payments.Checkout;

/// <summary>
/// The HMAC-SHA256 signatures of Zalo's Checkout SDK and of payOS, as hex in lower case, as the payment sandbox computed them (payment-sandbox/lib.mjs). The texts are
/// built exactly as the other side builds them: a different order, separator or JSON breaks the signature.
/// </summary>
internal static class CheckoutSignatures
{
    /// <summary>The JSON of the Mini App: camelCase, the property order of the record, the default escaping (the values are plain ASCII).</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string HmacHex(string key, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    /// <summary>
    /// The text of a <c>createOrder</c> mac: the keys in order (<c>amount, desc, extradata, item, method</c>), an object as its JSON, <c>extradata</c> and <c>method</c>
    /// as the JSON texts they already are.
    /// </summary>
    public static string CreateOrderText(long amount, string desc, string extradata, string itemJson, string method) =>
        $"amount={amount}&desc={desc}&extradata={extradata}&item={itemJson}&method={method}";

    /// <summary>The text of a <c>custom-callback-payment</c> mac: a fixed order with the private key itself last.</summary>
    public static string CallbackText(string appId, string orderId, int resultCode, string privateKey) =>
        $"appId={appId}&orderId={orderId}&resultCode={resultCode}&privateKey={privateKey}";

    /// <summary>The text payOS signs when a link is made: these five fields in alphabetical order.</summary>
    public static string PayOsLinkText(long amount, string cancelUrl, string description, long orderCode, string returnUrl) =>
        $"amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}";

    /// <summary>
    /// The text payOS signs over the <c>data</c> of a notification: the keys in alphabetical order, null, "null" and "undefined" as empty, a number as written, an
    /// object or a list as its JSON.
    /// </summary>
    public static string PayOsDataText(JsonElement data) =>
        string.Join('&', data.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={ValueText(p.Value)}"));

    /// <summary>The <c>createOrder</c> parameters for the custom payment method, signed with the Checkout private key.</summary>
    public static ZaloCheckoutOrder SignOrder(CheckoutOptions options, long amount, string desc, IReadOnlyList<ZaloCheckoutItem> items, string extradata)
    {
        var method = JsonSerializer.Serialize(new { id = options.ZaloCustomMethodId.Trim(), isCustom = true });
        var itemJson = JsonSerializer.Serialize(items, Json);
        var mac = HmacHex(options.ZaloPrivateKey.Trim(), CreateOrderText(amount, desc, extradata, itemJson, method));
        return new ZaloCheckoutOrder(amount, desc, items, extradata, method, mac);
    }

    /// <summary>Whether <paramref name="signature"/> is that of <paramref name="text"/>, compared in constant time.</summary>
    public static bool Matches(string key, string text, string signature)
    {
        var expected = Encoding.ASCII.GetBytes(HmacHex(key, text));
        var given = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() is "null" or "undefined" ? "" : value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };
}
