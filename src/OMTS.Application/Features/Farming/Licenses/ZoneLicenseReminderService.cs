using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>
/// GFR-03 for zone licenses, one license per call so that a failing license is skipped without touching the others. Only
/// the newest license of an In Use zone is reminded (a Replaced license and the licenses of a discontinued zone are not):
/// once when it becomes Expiring soon (MSG-FARM-01) and once when it becomes Expired (MSG-FARM-02), to the active
/// Cooperative Owners only, as the mandatory type ZONE_LICENSE_EXPIRY (BR-ACCOUNT-11). The markers make a second run on
/// the same day, a catch-up or a discontinue and resume of the zone send nothing twice: after a resume, the newest license
/// gets the notice of its current state at the next check if that notice was not sent yet.
/// </summary>
public sealed class ZoneLicenseReminderService(
    IZoneLicenseRepository licenses,
    IZoneRecipientQuery recipients,
    INotificationDispatcher notifications,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Licenses that may have a notice due on <paramref name="today"/>; <see cref="RemindAsync"/> re-checks each one.</summary>
    public Task<IReadOnlyList<Guid>> CandidateIdsAsync(DateOnly today, CancellationToken cancellationToken) =>
        licenses.ListReminderCandidateIdsAsync(today, LicenseStateRules.ExpiringSoonDays, cancellationToken);

    /// <summary>Sends the notice that is due on <paramref name="today"/>, if any; true when one was sent.</summary>
    public async Task<bool> RemindAsync(Guid documentId, DateOnly today, CancellationToken cancellationToken)
    {
        var document = await licenses.GetForEditAsync(documentId, cancellationToken);
        if (document is not { ExpiryDate: { } expiry } || document.Zone.StatusId != StatusIds.Usage.InUse)
        {
            return false;
        }

        var newest = ZoneLicenseOverview.Newest(document.ZoneId, await licenses.ListForZonesAsync([document.ZoneId], cancellationToken));
        if (newest?.Document.Id != document.Id)
        {
            return false;
        }

        var state = LicenseStateRules.StateOn(ZoneLicenseOverview.Facts(newest), today);
        if (ExpiryReminderRule.Due(state == LicenseState.ExpiringSoon, state == LicenseState.Expired, document) is not { } notice)
        {
            return false;
        }

        ExpiryReminderRule.MarkSent(document, notice, timeProvider.GetUtcNow());
        var typeName = LicenseTypes.NameOf(document.DocumentType);
        var zoneCode = document.Zone.Code;
        var expired = notice == ExpiryNotice.Expired;
        await notifications.NotifyAsync(
            await recipients.GetCooperativeOwnersAsync(cancellationToken),
            new NotificationContent(
                NotificationTypes.ZoneLicenseExpiry,
                expired ? "Zone license expired" : "Zone license expiring soon",
                expired
                    ? UserMessages.LicenseExpired(document.DocumentType, typeName, document.DocumentNumber, zoneCode, expiry)
                    : UserMessages.LicenseExpiringSoon(document.DocumentType, typeName, document.DocumentNumber, zoneCode, expiry, expiry.DayNumber - today.DayNumber),
                new RelatedRecord(RelatedRecordTypes.ZoneLicense, document.Id, document.DocumentNumber)),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
