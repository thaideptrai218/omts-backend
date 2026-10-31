using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// ACCOUNT-07 – the user chooses, per notification type, whether it is also pushed (BR-ACCOUNT-11). Every notification
/// stays in the list whatever the choice (BR-ACCOUNT-12); mandatory types are always pushed and shown so; types that
/// are never pushed are not offered. Saving creates no notification and changes no access.
/// </summary>
public sealed class NotificationPreferenceService(
    IAccountRepository accounts,
    PermissionService permissions,
    IPushDeviceRepository devices,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string TypeField = "Notification type";

    public async Task<Result<NotificationPreferencesView>> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        var offered = await OfferedTypesAsync(account, cancellationToken);
        return new NotificationPreferencesView(Views(account, offered), await PushAllowedAsync(account.Id, cancellationToken));
    }

    public async Task<Result<NotificationPreferencesSaved>> SaveAsync(
        Guid accountId, SaveNotificationPreferencesRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        var offered = (await OfferedTypesAsync(account, cancellationToken)).ToDictionary(t => t.Code, StringComparer.Ordinal);
        var changes = new List<(NotificationTypeDefinition Type, bool Push)>();
        foreach (var item in request.Items ?? [])
        {
            // A type the account cannot receive, and a mandatory type turned off, are not choices of SCR-ACCOUNT-06.
            if (item is null || item.Type is null || !offered.TryGetValue(item.Type, out var type) || (type.IsMandatory && !item.Push))
            {
                return AppError.Invalid(TypeField);
            }

            if (!type.IsMandatory)
            {
                changes.Add((type, item.Push));
            }
        }

        foreach (var (type, push) in changes)
        {
            Set(account, type.Code, push);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // ACCOUNT-07.1: the choice is saved; the user is told that no device of the account allows notifications.
        var pushAllowed = await PushAllowedAsync(account.Id, cancellationToken);
        var saved = UserMessages.Saved("Notification preferences");
        var notice = !pushAllowed && changes.Any(c => c.Push) ? UserMessages.PushNotAllowedOnDevice : null;
        return new NotificationPreferencesSaved(
            Views(account, offered.Values), pushAllowed, saved.Text, saved.Code, notice?.Text, notice?.Code);
    }

    private async Task<IReadOnlyList<NotificationTypeDefinition>> OfferedTypesAsync(Account account, CancellationToken cancellationToken)
    {
        var access = await permissions.GetAccessAsync(account.Id, cancellationToken);
        return NotificationTypes.OfferedTo(NotificationRoles.Of(account.SubjectType, access?.Grants ?? []));
    }

    private static IReadOnlyList<NotificationTypePreferenceView> Views(Account account, IEnumerable<NotificationTypeDefinition> types) =>
        NotificationTypes.All
            .Where(types.Contains)
            .Select(t => new NotificationTypePreferenceView(t.Code, t.Name, t.IsMandatory, NotificationDispatcher.IsPushed(t, account.NotificationPreferences)))
            .ToList();

    private static void Set(Account account, string type, bool push)
    {
        var existing = account.NotificationPreferences.FirstOrDefault(p => p.Type == type);
        if (existing is null)
        {
            account.NotificationPreferences.Add(new NotificationPreference { Type = type, Push = push });
        }
        else
        {
            existing.Push = push;
        }
    }

    private async Task<bool> PushAllowedAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await devices.ListDeliverableAsync([accountId], timeProvider.GetUtcNow(), cancellationToken)).Count > 0;
}
