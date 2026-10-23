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
/// ACS-02 phone step: traders and staff need a verified phone number before other functions (MSG67).
/// A trader enters a number; staff confirm the number the Owner entered or enter another one.
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

        var input = request.PhoneNumber ?? (account.SubjectType == SubjectType.Staff ? account.PhoneNumber : null);
        var phone = PhoneNumbers.Normalize(input);
        if (phone is null || await IsUsedByAnotherAccountAsync(phone, account.Id, cancellationToken))
        {
            return AppError.Invalid("Phone number");
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

        var token = await verification.ConsumeTokenAsync(request.VerificationToken, [VerificationPurpose.VerifyPhone], account.Id, null, cancellationToken);
        if (!token.IsSuccess)
        {
            return token.Error!;
        }

        var phone = token.Value!.Target;
        if (await IsUsedByAnotherAccountAsync(phone, account.Id, cancellationToken))
        {
            return AppError.Invalid("Phone number");
        }

        var before = new { account.PhoneNumber, account.PhoneVerifiedAt };
        account.PhoneNumber = phone;
        account.PhoneVerifiedAt = timeProvider.GetUtcNow();
        var hidden = account.SubjectType == SubjectType.Trader && await customerLinker.ApplyVerifiedPhoneAsync(account, phone, cancellationToken);

        audit.Record(new AuditEntry("Account.PhoneVerified", nameof(Account))
        {
            RecordId = account.Id,
            RecordCode = account.Username,
            Before = before,
            After = new { account.PhoneNumber, account.PhoneVerifiedAt, OldDocumentsHidden = hidden },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ContactConfirmed(phone, hidden, hidden ? UserMessages.OldDocumentsHidden.Text : null);
    }

    private async Task<bool> IsUsedByAnotherAccountAsync(string phone, Guid accountId, CancellationToken cancellationToken) =>
        await accounts.FindByPhoneAsync(phone, cancellationToken) is { } owner && owner.Id != accountId;
}
