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

/// <summary>Contact change started: the code went to the new contact; the old one stays in use until verified (MSG62).</summary>
public sealed record ContactChangeStarted(Verification.Dtos.VerificationStarted Verification, string Message);

/// <summary>
/// ACS-05 – the holder edits their own profile. A new email or phone number replaces the old one only once
/// its code is verified; username, subject type, roles and zones are read-only here.
/// </summary>
public sealed class ProfileService(
    IAccountRepository accounts,
    IAccessGrantRepository grants,
    VerificationService verification,
    TraderCustomerLinker customerLinker,
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

    public async Task<Result<(ProfileView Profile, string Message)>> UpdateAsync(
        Guid accountId, UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        if (request.Username is not null)
        {
            return new AppError(ErrorKind.Validation, UserMessages.UsernameReadOnly);
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

        if (await rowVersions.CheckAsync(account, request.RowVersion, cancellationToken) is { } conflict)
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

        return (await ViewAsync(account, cancellationToken), UserMessages.Saved("Personal profile").Text);
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

        var target = ContactInputs.Normalize(request.Channel, request.Target);
        // Re-entering the current contact is refused once it is verified; an unverified one (entered by the
        // Owner) can still be verified here.
        var currentVerified = request.Channel == VerificationChannel.Sms
            ? account.PhoneVerifiedAt is null ? null : account.PhoneNumber
            : account.EmailVerifiedAt is null ? null : account.Email;
        if (target is null || target == currentVerified || await IsUsedByAnotherAccountAsync(request.Channel, target, account.Id, cancellationToken))
        {
            return AppError.Invalid(ContactInputs.FieldName(request.Channel));
        }

        var purpose = request.Channel == VerificationChannel.Sms
            ? VerificationPurpose.ChangePhone
            : account.EmailVerifiedAt is null ? VerificationPurpose.VerifyEmail : VerificationPurpose.ChangeEmail;
        var started = await verification.StartAsync(purpose, request.Channel, target, account.Id, cancellationToken);
        return started.IsSuccess
            ? new ContactChangeStarted(started.Value!, UserMessages.CodeSentToNewContact(started.Value!.MaskedTarget, ContactInputs.ContactName(request.Channel)).Text)
            : started.Error!;
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
            return AppError.Invalid(ContactInputs.FieldName(channel));
        }

        var now = timeProvider.GetUtcNow();
        var hidden = false;
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
        return new ContactConfirmed(target, hidden, hidden ? UserMessages.OldDocumentsHidden.Text : UserMessages.Saved("Personal profile").Text);
    }

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
            RowVersionGuard.Encode(account.RowVersion));
    }
}
