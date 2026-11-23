using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Models.Payments;

namespace OMTS.Infrastructure.Payments.Checkout;

/// <summary>
/// payOS (https://payos.vn/docs/): <c>POST v2/payment-requests</c> makes the link and answers its VietQR text. Only the order code, the amount and the payment code are
/// sent, nothing about the trader. The keys never appear in a log line.
/// </summary>
public sealed class PayOsGateway(HttpClient http, IOptions<CheckoutOptions> options, ILogger<PayOsGateway> logger) : IPayOsGateway
{
    public bool IsConfigured => options.Value is { Provider: CheckoutOptions.PayOsProvider, HasPayOsKeys: true };

    public PayOsAccount? LinkedAccount => options.Value.LinkedAccount;

    public async Task<PayOsLink?> CreateLinkAsync(PayOsLinkRequest request, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!IsConfigured)
        {
            return null;
        }

        var back = settings.ReturnUrlOrDeeplink;
        var signature = CheckoutSignatures.HmacHex(
            settings.PayOsChecksumKey.Trim(), CheckoutSignatures.PayOsLinkText(request.Amount, back, request.Description, request.OrderCode, back));
        using var message = new HttpRequestMessage(HttpMethod.Post, "v2/payment-requests")
        {
            Content = JsonContent.Create(new
            {
                orderCode = request.OrderCode,
                amount = request.Amount,
                description = request.Description,
                cancelUrl = back,
                returnUrl = back,
                expiredAt = request.ExpiresAt.ToUnixTimeSeconds(),
                signature,
            }),
        };
        message.Headers.Add("x-client-id", settings.PayOsClientId.Trim());
        message.Headers.Add("x-api-key", settings.PayOsApiKey.Trim());
        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = answer.RootElement;
            if (root.ValueKind == JsonValueKind.Object && Text(root, "code") == "00"
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && Text(data, "qrCode") is { Length: > 0 } qr)
            {
                return new PayOsLink(
                    Text(data, "paymentLinkId") ?? "", qr, Text(data, "bin") ?? "", Text(data, "accountNumber") ?? "", Text(data, "accountName") ?? "",
                    Text(data, "description") ?? request.Description);
            }

            logger.LogWarning(
                "payOS refused payment link {OrderCode}: HTTP {Status}, code {Code}, {Description}",
                request.OrderCode, (int)response.StatusCode, root.ValueKind == JsonValueKind.Object ? Text(root, "code") : null,
                root.ValueKind == JsonValueKind.Object ? Text(root, "desc") : null);
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("payOS did not answer for payment link {OrderCode} ({ExceptionType}).", request.OrderCode, exception.GetType().Name);
            return null;
        }
    }

    public PayOsNotification Read(byte[] body) =>
        IsConfigured ? PayOsNotificationReader.Read(body, options.Value.PayOsChecksumKey) : PayOsNotification.NotAuthentic;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
