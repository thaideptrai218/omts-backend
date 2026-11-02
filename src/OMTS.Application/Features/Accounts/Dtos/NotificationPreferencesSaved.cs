namespace OMTS.Application.Features.Accounts.Dtos;

public sealed record NotificationPreferencesSaved(
    IReadOnlyList<NotificationTypePreferenceView> Types,
    bool PushAllowedOnDevice,
    string Message,
    string MessageCode,
    string? Notice,
    string? NoticeCode);
