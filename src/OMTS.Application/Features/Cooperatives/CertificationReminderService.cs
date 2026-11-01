using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.Features.Cooperatives;

/// <summary>
/// GFR-03 for cooperative certifications, one certification per call so that a failing one is skipped without touching
/// the others: once when it becomes Expiring soon (MSG-FARM-01) and once when it becomes Expired (MSG-FARM-02), to the
/// active Cooperative Owners only, as the mandatory type COOPERATIVE_CERTIFICATION_EXPIRY (BR-ACCOUNT-11). The markers
/// make a second run on the same day or a catch-up send nothing twice; a new expiry date re-arms both.
/// </summary>
public sealed class CertificationReminderService(
    ICooperativeRepository cooperatives,
    IZoneRecipientQuery recipients,
    INotificationDispatcher notifications,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Certifications that may have a notice due on <paramref name="today"/>; <see cref="RemindAsync"/> re-checks each one.</summary>
    public Task<IReadOnlyList<Guid>> CandidateIdsAsync(DateOnly today, CancellationToken cancellationToken) =>
        cooperatives.ListCertificationReminderCandidateIdsAsync(today, LicenseStateRules.ExpiringSoonDays, cancellationToken);

    /// <summary>Sends the notice that is due on <paramref name="today"/>, if any; true when one was sent.</summary>
    public async Task<bool> RemindAsync(Guid certificationId, DateOnly today, CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative
            || await cooperatives.GetCertificationAsync(cooperative.Id, certificationId, cancellationToken) is not { ExpiryDate: { } expiry } certification)
        {
            return false;
        }

        var state = CertificationStateRules.StateOn(expiry, today);
        if (ExpiryReminderRule.Due(state == CertificationState.ExpiringSoon, state == CertificationState.Expired, certification) is not { } notice)
        {
            return false;
        }

        // With no active Owner the notice stays due: it goes out once an Owner can receive it (once per state, GFR-03).
        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        if (owners.Count == 0)
        {
            return false;
        }

        ExpiryReminderRule.MarkSent(certification, notice, timeProvider.GetUtcNow());
        var expired = notice == ExpiryNotice.Expired;
        await notifications.NotifyAsync(
            owners,
            new NotificationContent(
                NotificationTypes.CooperativeCertificationExpiry,
                expired ? "Certification expired" : "Certification expiring soon",
                expired
                    ? UserMessages.CertificationExpired(certification.Name, certification.Number, expiry)
                    : UserMessages.CertificationExpiringSoon(certification.Name, certification.Number, expiry, expiry.DayNumber - today.DayNumber),
                new RelatedRecord(RelatedRecordTypes.CooperativeCertification, certification.Id, certification.Number ?? RecordCode(certification.Name))),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The record code of a notification holds at most 100 characters; a name may hold 200.
    private static string RecordCode(string name) => name.Length <= 100 ? name : name[..100];
}
