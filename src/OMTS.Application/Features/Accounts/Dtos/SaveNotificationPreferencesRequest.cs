namespace OMTS.Application.Features.Accounts.Dtos;

public sealed record SaveNotificationPreferencesRequest(IReadOnlyList<NotificationPushChange>? Items);
