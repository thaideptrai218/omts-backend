using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.EmployeeApp;

public sealed class EmployeeNotifier(IEmployeeAppRepository app, IPushOutbox pushOutbox, TimeProvider timeProvider) : IEmployeeNotifier
{
    public async Task NotifyAsync(Guid employeeId, EmployeeNotification notification, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Title);

        var target = await app.GetPushTargetAsync(employeeId, timeProvider.GetUtcNow(), cancellationToken);
        IReadOnlyList<string> tokens = target is { PushPermissionGranted: true, PushToken: { } token } ? [token] : [];
        pushOutbox.Enqueue(new PushMessage(
            null,
            notification.Type,
            notification.Title,
            notification.Message.Text,
            notification.RelatedRecordType,
            notification.RelatedRecordCode,
            tokens,
            MessageCode: notification.Message.Code,
            RelatedRecordId: notification.RelatedRecordId,
            MessageParams: MessageParameterJson.Serialize(notification.Message.Parameters),
            RecipientEmployeeId: employeeId));
    }
}
