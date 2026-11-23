using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Models.Payments;

namespace OMTS.Infrastructure.Payments.Checkout;

/// <summary>
/// Stands in for payOS in Development (<c>Checkout:Provider</c> = <c>Fake</c>): nothing leaves the server and no key is needed to start a checkout. A link is made up
/// with a QR text that pays nothing; the notifications are still checked with <c>Checkout:PayOsChecksumKey</c> (none accepted without it), so a test signs them as payOS
/// would. In Development the transfer can also be simulated with <c>POST api/dev/payment-notifications</c> and the payment code as the content.
/// </summary>
public sealed class FakePayOsGateway(IOptions<CheckoutOptions> options) : IPayOsGateway
{
    public const string BankBin = "970436";
    public const string AccountNumber = "FAKE00000001";

    /// <summary>The links asked for, in order: what a test reads.</summary>
    public ConcurrentQueue<PayOsLinkRequest> Links { get; } = new();

    /// <summary>The next links refused as payOS would when it does not answer (tests).</summary>
    public int RefuseNext { get; set; }

    public bool IsConfigured => true;

    public PayOsAccount? LinkedAccount => options.Value.LinkedAccount;

    public Task<PayOsLink?> CreateLinkAsync(PayOsLinkRequest request, CancellationToken cancellationToken = default)
    {
        if (RefuseNext > 0)
        {
            RefuseNext--;
            return Task.FromResult<PayOsLink?>(null);
        }

        Links.Enqueue(request);
        return Task.FromResult<PayOsLink?>(new PayOsLink(
            $"fake-{request.OrderCode}", $"FAKE-PAYOS|{request.OrderCode}|{request.Amount}|{request.Description}", BankBin, AccountNumber, "HTX DEMO", request.Description));
    }

    public PayOsNotification Read(byte[] body) => PayOsNotificationReader.Read(body, options.Value.PayOsChecksumKey);
}
