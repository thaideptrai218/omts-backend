using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// ACS-07 – the user chooses notification categories and channels. Saving creates no notifications and
/// changes no permission. Mandatory categories (water alerts, new orders, traceability QR sent) are always
/// delivered in-app and by push (MSG23).
/// </summary>
public sealed class NotificationPreferenceService(
    IAccountRepository accounts,
    IPushDeviceRepository devices,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private static readonly NotificationChannel[] OfferedChannels =
        [NotificationChannel.InApp, NotificationChannel.Push, NotificationChannel.Email, NotificationChannel.Zalo];

    public async Task<Result<IReadOnlyList<CategoryPreferenceView>>> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        return account is null ? AppError.NotFound : Result<IReadOnlyList<CategoryPreferenceView>>.Success(await ViewAsync(account, cancellationToken));
    }

    public async Task<Result<NotificationPreferencesSaved>> SaveAsync(
        Guid accountId, SaveNotificationPreferencesRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        var categories = CategoriesFor(account.SubjectType);
        var pushEligible = await HasPushDeviceAsync(account.Id, cancellationToken);
        var pushDeclined = false;
        foreach (var item in request.Items ?? [])
        {
            if (!categories.Contains(item.Category) || !OfferedChannels.Contains(item.Channel))
            {
                return AppError.Invalid("Notification item");
            }

            if (!item.Enabled && IsLocked(item.Category, item.Channel))
            {
                return new AppError(ErrorKind.Validation, UserMessages.MandatoryNotification);
            }

            if (item.Enabled && item.Channel is NotificationChannel.Email or NotificationChannel.Zalo
                && IneligibleReason(account, item.Channel, pushEligible) is not null)
            {
                return new AppError(ErrorKind.Validation, UserMessages.ChannelNotEligible(ChannelName(item.Channel)));
            }
        }

        foreach (var item in request.Items ?? [])
        {
            // Push without a device that allows it is saved off; the other channels are still saved (MSG-ACCOUNT-21).
            var enabled = item.Enabled;
            if (enabled && item.Channel == NotificationChannel.Push && !pushEligible && !IsLocked(item.Category, item.Channel))
            {
                enabled = false;
                pushDeclined = true;
            }

            Set(account, item.Category, item.Channel, enabled);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved("Notification settings");
        var notice = pushDeclined ? UserMessages.PushNotAllowedOnDevice : null;
        return new NotificationPreferencesSaved(await ViewAsync(account, cancellationToken), saved.Text, saved.Code, notice?.Text, notice?.Code);
    }

    /// <summary>Categories that apply to the kind of account; only these are shown and accepted.</summary>
    public static IReadOnlyList<string> CategoriesFor(SubjectType subjectType) => subjectType switch
    {
        SubjectType.CooperativeOwner => NotificationCategories.All,
        SubjectType.Staff =>
        [
            NotificationCategories.Orders, NotificationCategories.Work, NotificationCategories.DeadlineReminders, NotificationCategories.WaterAlerts,
        ],
        SubjectType.Trader => [NotificationCategories.Orders, NotificationCategories.TraceabilityQrSent],
        _ => [],
    };

    private async Task<IReadOnlyList<CategoryPreferenceView>> ViewAsync(Account account, CancellationToken cancellationToken)
    {
        var pushEligible = await HasPushDeviceAsync(account.Id, cancellationToken);
        return CategoriesFor(account.SubjectType)
            .Select(category => new CategoryPreferenceView(
                category,
                NotificationCategories.Mandatory.Contains(category),
                OfferedChannels.Select(channel =>
                {
                    var reason = IneligibleReason(account, channel, pushEligible);
                    var enabled = IsLocked(category, channel) || (reason is null && IsEnabled(account, category, channel));
                    return new ChannelPreferenceView(channel, enabled, reason is null, reason);
                }).ToList()))
            .ToList();
    }

    // In-app and push of mandatory categories are always delivered (NotificationDispatcher).
    private static bool IsLocked(string category, NotificationChannel channel) =>
        NotificationCategories.Mandatory.Contains(category) && channel is NotificationChannel.InApp or NotificationChannel.Push;

    private static string? IneligibleReason(Account account, NotificationChannel channel, bool pushEligible) => channel switch
    {
        NotificationChannel.Email when account.EmailVerifiedAt is null => "EMAIL_NOT_VERIFIED",
        NotificationChannel.Zalo => "ZALO_NOT_CONFIGURED",
        NotificationChannel.Push when !pushEligible => "NO_PUSH_DEVICE",
        _ => null,
    };

    // Without a saved choice, in-app and push are on (as the dispatcher assumes); email and Zalo are off.
    private static bool IsEnabled(Account account, string category, NotificationChannel channel) =>
        account.NotificationPreferences.FirstOrDefault(p => p.Category == category && p.Channel == channel)?.Enabled
        ?? channel is NotificationChannel.InApp or NotificationChannel.Push;

    private static void Set(Account account, string category, NotificationChannel channel, bool enabled)
    {
        var existing = account.NotificationPreferences.FirstOrDefault(p => p.Category == category && p.Channel == channel);
        if (existing is null)
        {
            account.NotificationPreferences.Add(new NotificationPreference { Category = category, Channel = channel, Enabled = enabled });
        }
        else
        {
            existing.Enabled = enabled;
        }
    }

    private async Task<bool> HasPushDeviceAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await devices.ListDeliverableAsync([accountId], timeProvider.GetUtcNow(), cancellationToken)).Count > 0;

    private static string ChannelName(NotificationChannel channel) => channel == NotificationChannel.Zalo ? "Zalo OA" : channel.ToString();
}
