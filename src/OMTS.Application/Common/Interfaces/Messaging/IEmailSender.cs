using OMTS.Application.Common.Models.Messaging;
using OMTS.Application.Common.Models;

namespace OMTS.Application.Common.Interfaces.Messaging;

/// <summary>Email Service adapter (formerly API-01); provider chosen by <c>Email:Provider</c>.</summary>
public interface IEmailSender
{
    Task<DeliveryResult> SendAsync(
        string to,
        string subject,
        string htmlBody,
        string textBody,
        IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default);
}
