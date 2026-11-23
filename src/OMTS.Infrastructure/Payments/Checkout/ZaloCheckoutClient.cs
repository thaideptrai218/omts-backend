using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Models.Payments;

namespace OMTS.Infrastructure.Payments.Checkout;

/// <summary>
/// Zalo's Checkout SDK (https://mini.zalo.me/documents/checkout-sdk/): signs the orders and reports a paid order with
/// <c>POST api/transaction/{appId}/custom-callback-payment</c>. It goes through <c>Zalo:ProxyUrl</c> like the other Zalo calls (Zalo refuses callers outside Vietnam).
/// </summary>
public sealed class ZaloCheckoutClient(HttpClient http, IOptions<CheckoutOptions> options, ILogger<ZaloCheckoutClient> logger) : IZaloCheckout
{
    /// <summary><c>resultCode</c> of a payment that succeeded (0 refunded, -1 failed).</summary>
    private const int Paid = 1;

    public bool IsConfigured => options.Value is { Provider: CheckoutOptions.PayOsProvider, HasZaloKeys: true };

    public ZaloCheckoutOrder Sign(long amount, string description, IReadOnlyList<ZaloCheckoutItem> items, string extradata) =>
        CheckoutSignatures.SignOrder(options.Value, amount, description, items, extradata);

    public async Task<ZaloPaymentReport> ReportPaidAsync(string zaloOrderId, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!IsConfigured)
        {
            return ZaloPaymentReport.Failed("Checkout is not configured.");
        }

        var appId = settings.ZaloMiniAppId.Trim();
        var key = settings.ZaloPrivateKey.Trim();
        var mac = CheckoutSignatures.HmacHex(key, CheckoutSignatures.CallbackText(appId, zaloOrderId, Paid, key));
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"api/transaction/{Uri.EscapeDataString(appId)}/custom-callback-payment", new { appId, orderId = zaloOrderId, resultCode = Paid, mac }, cancellationToken);
            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = answer.RootElement;
            var error = root.ValueKind == JsonValueKind.Object ? Number(root, "error") ?? Number(root, "err") : null;
            if (response.IsSuccessStatusCode && error == 0)
            {
                return ZaloPaymentReport.Done;
            }

            var text = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null;
            return ZaloPaymentReport.Failed($"HTTP {(int)response.StatusCode}, error {error?.ToString() ?? "none"}: {text}");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Zalo did not answer the payment report of order {ZaloOrderId} ({ExceptionType}).", zaloOrderId, exception.GetType().Name);
            return ZaloPaymentReport.Failed($"No answer ({exception.GetType().Name}).");
        }
    }

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
