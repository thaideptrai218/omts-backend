using System.Security.Cryptography;
using System.Text.Json;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Checkout;

/// <summary>The fixed rules of a trader's Mini App checkout (decision of 21/11/2026).</summary>
public static class TraderCheckoutRules
{
    /// <summary>How long a payOS link takes money, as the payment sandbox did; after it the trader starts a new checkout.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>A checkout this close to its end is not handed out again: the trader would not have the time to pay it.</summary>
    public static readonly TimeSpan ReuseMargin = TimeSpan.FromMinutes(2);

    /// <summary>Zalo is told again about a paid order at most this often, and at most <see cref="MaxZaloReports"/> times (CLAUDE.md §10: until it succeeds).</summary>
    public static readonly TimeSpan ZaloReportGap = TimeSpan.FromMinutes(1);

    public const int MaxZaloReports = 30;

    public const int MaxZaloOrderIdLength = 100;

    public const int MaxZaloErrorLength = 500;

    /// <summary>The note of the payment, read by the Accountant.</summary>
    public const string PaymentNote = "Thương lái trả qua Zalo Mini App (payOS)";

    /// <summary>Why an older payment of the invoice still waiting for its transfer is cancelled when the trader starts a new checkout.</summary>
    public const string ReplacedReason = "Lần thanh toán qua Zalo đã hết hạn hoặc số tiền đã đổi; thương lái đã tạo lần mới.";

    /// <summary>What Zalo shows the trader; plain letters, as the payment sandbox sent.</summary>
    public static string Description(string invoiceCode) => $"Thanh toan hoa don {invoiceCode}";

    /// <summary>The JSON text Zalo gives back with the order.</summary>
    public static string Extradata(Guid checkoutId) => JsonSerializer.Serialize(new { checkoutId });

    /// <summary>A payOS order code of nine digits: payOS takes a positive integer the Mini App can hold exactly.</summary>
    public static long NewOrderCode() => RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000);

    /// <summary>The payment arrived with at least what was asked: only then is Zalo told it was paid (a short transfer is recorded as a part payment, never as a success).</summary>
    public static bool PaidInFull(TraderCheckout checkout) =>
        checkout.Payment.StatusId == StatusIds.CustomerPayment.Completed && checkout.Payment.Amount >= checkout.Amount;

    /// <summary>The checkout may still be paid as it is: its payment waits, its link takes money long enough, and it asks what is left to pay now.</summary>
    public static bool Reusable(TraderCheckout checkout, decimal unpaid, DateTimeOffset now) =>
        checkout.Payment.StatusId == StatusIds.CustomerPayment.PendingTransfer && checkout.ExpiresAt - ReuseMargin > now && checkout.Amount == unpaid;

    public static string? Truncated(string? text, int length) => text is null ? null : text.Length <= length ? text : text[..length];
}
