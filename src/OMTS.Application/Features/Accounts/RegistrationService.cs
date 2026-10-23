using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Verification;
using OMTS.Application.Features.Verification.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts;

/// <summary>ACS-01 – a trader opens an account with a verified phone number or email.</summary>
public sealed class RegistrationService(
    IAccountRepository accounts,
    ICooperativeRepository cooperatives,
    VerificationService verification,
    TraderCustomerLinker customerLinker,
    UsernameGenerator usernames,
    IPasswordHasher passwordHasher,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Checks the format and that no account uses the identifier, then sends the code.</summary>
    public async Task<Result<VerificationStarted>> StartVerificationAsync(StartVerificationRequest request, CancellationToken cancellationToken)
    {
        var identifier = ContactInputs.Normalize(request.Channel, request.Target);
        if (identifier is null)
        {
            return AppError.Invalid(ContactInputs.FieldName(request.Channel));
        }

        if (await IsTakenAsync(request.Channel, identifier, cancellationToken))
        {
            return AppError.Conflict(UserMessages.CannotRegister);
        }

        return await verification.StartAsync(VerificationPurpose.Registration, request.Channel, identifier, null, cancellationToken);
    }

    public async Task<Result<TraderRegistered>> RegisterAsync(RegisterTraderRequest request, CancellationToken cancellationToken)
    {
        var identifier = ContactInputs.Normalize(request.Channel, request.Identifier);
        if (identifier is null)
        {
            return AppError.Invalid(ContactInputs.FieldName(request.Channel));
        }

        if (!PasswordPolicy.IsValid(request.Password) || request.Password != request.PasswordConfirmation)
        {
            return AppError.Invalid("Password");
        }

        if (await IsTakenAsync(request.Channel, identifier, cancellationToken))
        {
            return AppError.Conflict(UserMessages.CannotRegister);
        }

        var token = await verification.ConsumeTokenAsync(
            request.VerificationToken, [VerificationPurpose.Registration], null, identifier, cancellationToken);
        if (!token.IsSuccess)
        {
            return token.Error!;
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");

        var now = timeProvider.GetUtcNow();
        var isPhone = request.Channel == VerificationChannel.Sms;
        var trader = new Account
        {
            SubjectType = SubjectType.Trader,
            StatusId = StatusIds.Account.Active,
            Username = await usernames.NewTraderUsernameAsync(cancellationToken),
            // Display names hold 200 characters; an email may be longer.
            DisplayName = identifier.Length > 200 ? identifier[..200] : identifier,
            PhoneNumber = isPhone ? identifier : null,
            PhoneVerifiedAt = isPhone ? now : null,
            Email = isPhone ? null : identifier,
            EmailVerifiedAt = isPhone ? null : now,
            PasswordHash = passwordHasher.Hash(request.Password),
        };
        accounts.Add(trader);
        var (customer, hidden) = await customerLinker.LinkNewTraderAsync(trader, cooperative.Id, cancellationToken);

        audit.Record(new AuditEntry("Account.Registered", nameof(Account))
        {
            RecordId = trader.Id,
            RecordCode = trader.Username,
            ActorAccountId = trader.Id,
            After = new { trader.Username, trader.SubjectType, trader.PhoneNumber, trader.Email, CustomerId = customer.Id, OldDocumentsHidden = hidden },
        });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel registration took the phone or email between the check above and this save.
            return AppError.Conflict(UserMessages.CannotRegister);
        }

        return new TraderRegistered(trader.Id, trader.Username, hidden, hidden ? UserMessages.OldDocumentsHidden.Text : null);
    }

    private async Task<bool> IsTakenAsync(VerificationChannel channel, string identifier, CancellationToken cancellationToken) =>
        channel == VerificationChannel.Sms
            ? await accounts.FindByPhoneAsync(identifier, cancellationToken) is not null
            : await accounts.FindByVerifiedEmailAsync(identifier, cancellationToken) is not null;
}
