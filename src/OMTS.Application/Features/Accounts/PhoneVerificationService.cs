using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Verification;
using OMTS.Application.Features.Verification.Dtos;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// ACCOUNT-02.1: every account needs a verified phone number before other functions (BR-ACCOUNT-03, MSG-ACCOUNT-04).
/// A trader enters a number; staff and the Cooperative Owner confirm the number on the account or enter another one.
/// </summary>
public sealed class PhoneVerificationService(
    IAccountRepository accounts,
    VerificationService verification,
    TraderCustomerLinker customerLinker,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<VerificationStarted>> StartAsync(Guid accountId, StartPhoneVerificationRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        // A verified phone is changed through contact-changes, which asks for the current password.
        if (account.PhoneVerifiedAt is not null)
        {
            return AppError.Invalid("Phone number");
        }

        var input = request.PhoneNumber ?? (account.SubjectType == SubjectType.Trader ? null : account.PhoneNumber);
        var phone = PhoneNumbers.Normalize(input);
        if (phone is null)
        {
            return AppError.Invalid("Phone number");
        }

        if (await IsUsedByAnotherAccountAsync(phone, account.Id, cancellationToken))
        {
            return PhoneUsedByAnotherAccount();
        }

        return await verification.StartAsync(VerificationPurpose.VerifyPhone, VerificationChannel.Sms, phone, account.Id, cancellationToken);
    }

    public async Task<Result<ContactConfirmed>> ConfirmAsync(Guid accountId, ConfirmContactRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return AppError.NotFound;
        }

        // A token issued while the phone was unverified cannot replace a phone verified meanwhile (that is a
        // contact change, which needs the current password).
        if (account.PhoneVerifiedAt is not null)
        {
            return AppError.Invalid("Phone number");
        }

        var token = await verification.ConsumeTokenAsync(request.VerificationToken, [VerificationPurpose.VerifyPhone], account.Id, null, cancellationToken);
        if (!token.IsSuccess)
        {
            return token.Error!;
        }

        var phone = token.Value!.Target;
        if (await IsUsedByAnotherAccountAsync(phone, account.Id, cancellationToken))
        {
            return PhoneUsedByAnotherAccount();
        }

        var before = new { account.PhoneNumber, account.PhoneVerifiedAt };
        account.PhoneNumber = phone;
        account.PhoneVerifiedAt = timeProvider.GetUtcNow();
        if (account.SubjectType == SubjectType.Trader)
        {
            await customerLinker.ApplyVerifiedPhoneAsync(account, phone, cancellationToken);
        }

        audit.Record(new AuditEntry("Account.PhoneVerified", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            Before = before,
            After = new { account.PhoneNumber, account.PhoneVerifiedAt },
        });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // Another account took the number between the check above and this save (unique index).
            return PhoneUsedByAnotherAccount();
        }

        return new ContactConfirmed(phone, null, null);
    }

    private async Task<bool> IsUsedByAnotherAccountAsync(string phone, Guid accountId, CancellationToken cancellationToken) =>
        await accounts.FindByPhoneAsync(phone, cancellationToken) is { } owner && owner.Id != accountId;

    /// <summary>MSG-ACCOUNT-09 (MSG01 before).</summary>
    private static AppError PhoneUsedByAnotherAccount() =>
        AppError.UsedByAnotherAccount(ErrorKind.Validation, "Phone number", MessageCodes.Legacy.Msg01);
}
