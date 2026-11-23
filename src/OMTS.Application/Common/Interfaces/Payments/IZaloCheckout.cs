using OMTS.Application.Common.Models.Payments;

namespace OMTS.Application.Common.Interfaces.Payments;

/// <summary>
/// Zalo's Checkout SDK, required for every payment inside a Mini App (CLAUDE.md §10): the server signs the order the Mini App creates and tells Zalo once the money
/// arrived, both with the Checkout private key, which never leaves the server.
/// </summary>
public interface IZaloCheckout
{
    /// <summary>Whether the Mini App id, the private key and the custom method are set; without them no checkout can start.</summary>
    bool IsConfigured { get; }

    /// <summary>The <c>createOrder</c> parameters for the custom payment method, with their <c>mac</c>.</summary>
    ZaloCheckoutOrder Sign(long amount, string description, IReadOnlyList<ZaloCheckoutItem> items, string extradata);

    /// <summary>Tells Zalo the order was paid (<c>resultCode</c> 1); a failure is reported, never thrown, and the report is sent again later.</summary>
    Task<ZaloPaymentReport> ReportPaidAsync(string zaloOrderId, CancellationToken cancellationToken = default);
}
