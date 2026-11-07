using OMTS.Application.Common.Models;

namespace OMTS.Application.Common.Interfaces.Messaging;

/// <summary>
/// SMS Service adapter (formerly API-03); provider chosen by <c>Sms:Provider</c>. Implementations log
/// only the masked recipient and a message id, never the text, so verification codes never reach logs.
/// </summary>
public interface ISmsSender
{
    Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default);
}
