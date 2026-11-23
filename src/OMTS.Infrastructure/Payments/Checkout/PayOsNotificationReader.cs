using System.Globalization;
using System.Text.Json;
using OMTS.Application.Common.Models.Payments;

namespace OMTS.Infrastructure.Payments.Checkout;

/// <summary>
/// Reads a payOS notification: <c>{ code, desc, success, data: { orderCode, amount, description, reference, transactionDateTime, code, ... }, signature }</c>. It is
/// authentic only with the signature of the checksum key over <c>data</c>; nothing of a body that is not is read further.
/// </summary>
internal static class PayOsNotificationReader
{
    /// <summary>payOS writes the time of the transfer in Vietnam time.</summary>
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static PayOsNotification Read(byte[] body, string checksumKey)
    {
        if (string.IsNullOrWhiteSpace(checksumKey) || body.Length == 0)
        {
            return PayOsNotification.NotAuthentic;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.String
                || !CheckoutSignatures.Matches(checksumKey.Trim(), CheckoutSignatures.PayOsDataText(data), signature.GetString()!))
            {
                return PayOsNotification.NotAuthentic;
            }

            var successful = Text(root, "code") == "00" && Text(data, "code") == "00";
            if (!data.TryGetProperty("orderCode", out var orderCode) || !orderCode.TryGetInt64(out var code)
                || !data.TryGetProperty("amount", out var amount) || !amount.TryGetDecimal(out var money))
            {
                return new PayOsNotification(true, new PayOsTransfer(false, 0, 0m, "", null, null));
            }

            return new PayOsNotification(true, new PayOsTransfer(successful, code, money, Text(data, "reference") ?? "", Text(data, "description"), TimeOf(Text(data, "transactionDateTime"))));
        }
        catch (JsonException)
        {
            return PayOsNotification.NotAuthentic;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? TimeOf(string? text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, VietnamOffset)
            : null;
}
