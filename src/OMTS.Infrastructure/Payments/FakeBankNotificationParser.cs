using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Payments;

namespace OMTS.Infrastructure.Payments;

/// <summary>
/// The notifications of the fake Payment Notification Service (Development only), in a generic format until a provider is chosen:
/// <c>{ "transactions": [ { "bankReference", "accountNumber", "amount", "content", "occurredAt", "direction": "In" | "Out" } ] }</c>, signed with HMAC-SHA256 of the raw body
/// and <c>Payments:Fake:WebhookSecret</c>, sent as <c>X-Omts-Signature: sha256=&lt;hex&gt;</c>. Without a secret no notification is authentic.
/// </summary>
public sealed class FakeBankNotificationParser(IOptions<PaymentProviderOptions> options) : IBankNotificationParser
{
    public const string SignatureHeader = "X-Omts-Signature";
    public const string SignaturePrefix = "sha256=";
    public const int MaxTransactions = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The value of <see cref="SignatureHeader"/> for a body: what the service sends, and what the tests sign with.</summary>
    public static string Sign(byte[] body, string secret) =>
        SignaturePrefix + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    public bool IsAuthentic(BankNotificationRequest request)
    {
        var secret = options.Value.Fake.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret) || !request.Headers.TryGetValue(SignatureHeader, out var sent) || !sent.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(request.Body, secret));
        var given = Encoding.ASCII.GetBytes(sent.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public BankNotificationParse Parse(BankNotificationRequest request)
    {
        GenericNotification? notification;
        try
        {
            notification = JsonSerializer.Deserialize<GenericNotification>(request.Body, Json);
        }
        catch (JsonException)
        {
            return BankNotificationParse.Invalid("The body is not a notification in the generic format.");
        }

        if (notification?.Transactions is not { Count: > 0 and <= MaxTransactions } transactions)
        {
            return BankNotificationParse.Invalid($"A notification holds 1 to {MaxTransactions} transactions.");
        }

        var notices = new List<BankTransferNotice>(transactions.Count);
        foreach (var t in transactions)
        {
            if (t is null || t.Amount is null || t.OccurredAt is null || t.Direction is null)
            {
                return BankNotificationParse.Invalid("Each transaction has its amount, time and direction.");
            }

            notices.Add(new BankTransferNotice(t.BankReference ?? string.Empty, t.AccountNumber ?? string.Empty, t.Amount.Value, t.Content, t.OccurredAt.Value, t.Direction.Value));
        }

        return new BankNotificationParse(notices, null);
    }

    private sealed record GenericNotification(List<GenericTransaction?>? Transactions);

    private sealed record GenericTransaction(
        string? BankReference, string? AccountNumber, decimal? Amount, string? Content, DateTimeOffset? OccurredAt, BankTransferDirection? Direction);
}
