using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Verification;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// Contact change started: the code went to the new contact; the old one stays in use until verified
/// (MSG-ACCOUNT-19 in <paramref name="Message"/>, its code in <paramref name="MessageCode"/>).
/// </summary>
public sealed record ContactChangeStarted(Verification.Dtos.VerificationStarted Verification, string Message, string MessageCode);

/// <summary>
/// ACS-05 – the holder edits their own profile. A new email or phone number needs the current password and
/// replaces the old one only once its code is verified; the previous verified contact is told. Username,
/// subject type, roles and zones are read-only here.
/// </summary>
public sealed class ProfileService(
    IAccountRepository accounts,
    IAccessGrantRepository grants,
    VerificationService verification,
    TraderCustomerLinker customerLinker,
    CurrentPasswordProof currentPassword,
    ContactChangeNotifier notifier,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const int MaxDisplayNameLength = 200;

    public async Task<Result<ProfileView>> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        return account is null ? AppError.NotFound : await ViewAsync(account, cancellationToken);
    }

    public async Task<Result<(ProfileView Profile, UserMessage Message)>> UpdateAsync(
        Guid accountId, UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        // The username is read-only here (ACCOUNT-05): MSG-GEN-01 "Username" (MSG20 before).
        if (request.Username is not null)
        {
            return new AppError(
                ErrorKind.Validation,
                UserMessages.Invalid("Username") with { LegacyCode = MessageCodes.Legacy.Msg20 },
                new Dictionary<string, object?> { ["field"] = "Username" });
        }

        var name = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxDisplayNameLength)
        {
            return AppError.Invalid("Display name");
        }

        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return new AppError(ErrorKind.Unauthorized, UserMessages.SessionExpired);
        }

        if (await rowVersions.CheckAccountEditAsync(account, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (account.DisplayName != name)
        {
            audit.Record(new AuditEntry("Account.ProfileUpdated", nameof(Account))
            {
                RecordId = account.Id,
                RecordCode = account.Username,
                Before = new { account.DisplayName },
                After = new { DisplayName = name },
            });
            account.DisplayName = name;
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return (await ViewAsync(account, cancellationToken), UserMessages.Saved("Personal profile"));
    }

    /// <summary>Sends a code to the new email or phone number; nothing changes until it is verified.</summary>
    public async Task<Result<ContactChangeStarted>> StartContactChangeAsync(
        Guid accountId, StartContactChangeRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        if (await currentPassword.CheckAsync(account, request.CurrentPassword, cancellationToken) is { } refused)
        {
            return refused;
        }

        var target = ContactInputs.Normalize(request.Channel, request.Target);
        // Re-entering the current contact is refused once it is verified; an unverified one (entered by the
        // Owner) can still be verified here.
        var currentVerified = request.Channel == VerificationChannel.Sms
            ? account.PhoneVerifiedAt is null ? null : account.PhoneNumber
            : account.EmailVerifiedAt is null ? null : account.Email;
        if (target is null || target == currentVerified)
        {
            return AppError.Invalid(ContactInputs.FieldName(request.Channel));
        }

        if (await IsUsedByAnotherAccountAsync(request.Channel, target, account.Id, cancellationToken))
        {
            return UsedByAnotherAccount(request.Channel);
        }

        var purpose = request.Channel == VerificationChannel.Sms
            ? VerificationPurpose.ChangePhone
            : account.EmailVerifiedAt is null ? VerificationPurpose.VerifyEmail : VerificationPurpose.ChangeEmail;
        var started = await verification.StartAsync(purpose, request.Channel, target, account.Id, cancellationToken);
        if (!started.IsSuccess)
        {
            return started.Error!;
        }

        var sent = UserMessages.CodeSent(started.Value!.MaskedTarget, (int)VerificationCode.Lifetime.TotalMinutes);
        return new ContactChangeStarted(started.Value, sent.Text, sent.Code);
    }

    public async Task<Result<ContactConfirmed>> ConfirmContactChangeAsync(
        Guid accountId, ConfirmContactRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        var token = await verification.ConsumeTokenAsync(
            request.VerificationToken,
            [VerificationPurpose.ChangePhone, VerificationPurpose.ChangeEmail, VerificationPurpose.VerifyEmail],
            account.Id,
            null,
            cancellationToken);
        if (!token.IsSuccess)
        {
            return token.Error!;
        }

        var (channel, target) = (token.Value!.Channel, token.Value.Target);
        if (await IsUsedByAnotherAccountAsync(channel, target, account.Id, cancellationToken))
        {
            return UsedByAnotherAccount(channel);
        }

        var now = timeProvider.GetUtcNow();
        var hidden = false;
        var previous = channel == VerificationChannel.Sms
            ? account.PhoneVerifiedAt is null ? null : account.PhoneNumber
            : account.EmailVerifiedAt is null ? null : account.Email;
        object before;
        if (channel == VerificationChannel.Sms)
        {
            before = new { account.PhoneNumber };
            account.PhoneNumber = target;
            account.PhoneVerifiedAt = now;
            hidden = account.SubjectType == SubjectType.Trader && await customerLinker.ApplyVerifiedPhoneAsync(account, target, cancellationToken);
        }
        else
        {
            before = new { account.Email };
            account.Email = target;
            account.EmailVerifiedAt = now;
        }

        audit.Record(new AuditEntry("Account.ContactChanged", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            Before = before,
            After = channel == VerificationChannel.Sms ? new { account.PhoneNumber } : new { account.Email },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await notifier.NotifyPreviousContactAsync(channel, previous, target, cancellationToken);
        var notice = hidden ? UserMessages.OldDocumentsHidden : UserMessages.Saved("Personal profile");
        return new ContactConfirmed(target, hidden, notice.Text, notice.Code);
    }

    /// <summary>MSG-ACCOUNT-09 (MSG01 before).</summary>
    private static AppError UsedByAnotherAccount(VerificationChannel channel) =>
        AppError.UsedByAnotherAccount(ErrorKind.Validation, ContactInputs.FieldName(channel), MessageCodes.Legacy.Msg01);

    private async Task<bool> IsUsedByAnotherAccountAsync(VerificationChannel channel, string target, Guid accountId, CancellationToken cancellationToken)
    {
        var owner = channel == VerificationChannel.Sms
            ? await accounts.FindByPhoneAsync(target, cancellationToken)
            : await accounts.FindByVerifiedEmailAsync(target, cancellationToken);
        return owner is not null && owner.Id != accountId;
    }

    private async Task<ProfileView> ViewAsync(Account account, CancellationToken cancellationToken)
    {
        var today = Common.BusinessClock.ToBusinessDate(timeProvider.GetUtcNow());
        var effective = (await grants.ListForAccountAsync(account.Id, cancellationToken)).Where(g => g.IsEffectiveOn(today));
        return new ProfileView(
            account.Id,
            account.Username,
            account.DisplayName,
            account.SubjectType,
            account.Email,
            account.EmailVerifiedAt is not null,
            account.PhoneNumber,
            account.PhoneVerifiedAt is not null,
            effective.Select(g => new ProfileGrantView(g.Role.Name, g.ZoneId, g.Zone?.Name, g.ZoneId is null)).ToList(),
            AccountEditVersion.Of(account));
    }
}
