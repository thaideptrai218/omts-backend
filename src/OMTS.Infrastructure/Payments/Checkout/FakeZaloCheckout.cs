using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Models.Payments;

namespace OMTS.Infrastructure.Payments.Checkout;

/// <summary>
/// Stands in for Zalo's Checkout SDK in Development (<c>Checkout:Provider</c> = <c>Fake</c>): the orders are signed for real with <c>Checkout:ZaloPrivateKey</c> (empty
/// when unset, as no browser can create a Zalo order anyway), the reports of paid orders are kept in memory instead of being sent.
/// </summary>
public sealed class FakeZaloCheckout(IOptions<CheckoutOptions> options) : IZaloCheckout
{
    /// <summary>The Zalo orders reported as paid, in order (tests).</summary>
    public ConcurrentQueue<string> Reports { get; } = new();

    /// <summary>The next reports Zalo refuses (tests).</summary>
    public int RefuseNext { get; set; }

    public bool IsConfigured => true;

    public ZaloCheckoutOrder Sign(long amount, string description, IReadOnlyList<ZaloCheckoutItem> items, string extradata) =>
        CheckoutSignatures.SignOrder(options.Value, amount, description, items, extradata);

    public Task<ZaloPaymentReport> ReportPaidAsync(string zaloOrderId, CancellationToken cancellationToken = default)
    {
        if (RefuseNext > 0)
        {
            RefuseNext--;
            return Task.FromResult(ZaloPaymentReport.Failed("error -1: fake refusal"));
        }

        Reports.Enqueue(zaloOrderId);
        return Task.FromResult(ZaloPaymentReport.Done);
    }
}
