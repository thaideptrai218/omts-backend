using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>
/// FARM-03 expiring license reminder (formerly JOB-06), one license per call so that a failing license is
/// skipped without touching the others: one notice when 0…30 days are left and one more once it has expired.
/// The markers make a second run on the same day send nothing.
/// </summary>
public sealed class ZoneLicenseReminderService(
    IZoneLicenseRepository licenses,
    IZoneRecipientQuery recipients,
    INotificationDispatcher notifications,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Unreplaced licenses with a notice due on <paramref name="today"/>; <see cref="RemindAsync"/> re-checks each one.</summary>
    public Task<IReadOnlyList<Guid>> CandidateIdsAsync(DateOnly today, CancellationToken cancellationToken) =>
        licenses.ListReminderCandidateIdsAsync(today, LicenseStateRules.ExpiringSoonDays, cancellationToken);

    /// <summary>Sends the notice that is due on <paramref name="today"/>, if any; true when one was sent.</summary>
    public async Task<bool> RemindAsync(Guid documentId, DateOnly today, CancellationToken cancellationToken)
    {
        var document = await licenses.GetForEditAsync(documentId, cancellationToken);
        if (document?.ExpiryDate is not { } expiry || await licenses.FindReplacementAsync(document.Id, cancellationToken) is not null)
        {
            return false;
        }

        var daysLeft = expiry.DayNumber - today.DayNumber;
        var now = timeProvider.GetUtcNow();
        bool expired;
        if (daysLeft < 0 && document.ExpiredReminderSentAt is null)
        {
            expired = true;
            document.ExpiredReminderSentAt = now;
            // Never two notices for one license on the same run.
            document.ExpiringReminderSentAt ??= now;
        }
        else if (daysLeft is >= 0 and <= LicenseStateRules.ExpiringSoonDays && document.ExpiringReminderSentAt is null)
        {
            expired = false;
            document.ExpiringReminderSentAt = now;
        }
        else
        {
            return false;
        }

        var accounts = new HashSet<Guid>();
        foreach (var scope in document.ZoneScopes)
        {
            accounts.UnionWith(await recipients.GetRecipientsAsync(scope.ZoneId, PermissionFunctions.CultivationCycles, PermissionAction.View, cancellationToken));
        }

        var documentName = $"{LicenseTypes.Names.GetValueOrDefault(document.DocumentType, document.DocumentType)} {document.DocumentNumber}";
        var zoneNames = string.Join(", ", document.ZoneScopes.Select(s => s.Zone.Name).Order());
        var message = UserMessages.LicenseExpiry(documentName, zoneNames, Math.Max(daysLeft, 0));
        await notifications.NotifyAsync(
            accounts,
            NotificationTypes.ZoneLicenseExpiry,
            expired ? "Zone license expired" : "Zone license expiring soon",
            message.Text,
            "ZoneComplianceDocument",
            document.Id.ToString(),
            isMandatory: false,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
