using OMTS.Application.Common.Models.Payments;

namespace OMTS.Application.Common.Interfaces.Payments;

/// <summary>
/// payOS, the third party of the Mini App payments (decision of 21/11/2026): it makes the QR code of a payment link and reports the transfer that pays it. The keys of the
/// payOS channel stay on the server. The web does not use it: its VietQR codes and the bank notifications stay as they are (<see cref="IBankNotificationParser"/>).
/// </summary>
public interface IPayOsGateway
{
    /// <summary>Whether the channel keys are set; without them no checkout can start.</summary>
    bool IsConfigured { get; }

    /// <summary>The bank account linked to the payOS channel when it is set; null records the payments against the cooperative's receiving bank account.</summary>
    PayOsAccount? LinkedAccount { get; }

    /// <summary>A new payment link, or null when payOS refused it or did not answer (the adapter logs why).</summary>
    Task<PayOsLink?> CreateLinkAsync(PayOsLinkRequest request, CancellationToken cancellationToken = default);

    /// <summary>A notification body as it reached the webhook: authentic only with the signature of the checksum key over its <c>data</c>.</summary>
    PayOsNotification Read(byte[] body);
}
