namespace OMTS.Application.Features.Accounts.Dtos;

public sealed record NotificationPreferencesView(IReadOnlyList<NotificationTypePreferenceView> Types, bool PushAllowedOnDevice);
