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

/// <summary>ACS-01 – a trader opens an account with a verified phone number. Email is a contact only, never a way to register.</summary>
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
    /// <summary>Checks the format and that no account uses the phone number, then sends the code.</summary>
    public async Task<Result<VerificationStarted>> StartVerificationAsync(StartVerificationRequest request, CancellationToken cancellationToken)
    {
        if (RefusalOfChannel(request.Channel) is { } refused)
        {
            return refused;
        }

        var identifier = PhoneNumbers.Normalize(request.Target);
        if (identifier is null)
        {
            return AppError.Invalid(PhoneField);
        }

        if (await accounts.FindByPhoneAsync(identifier, cancellationToken) is not null)
        {
            return AlreadyRegistered();
        }

        return await verification.StartAsync(VerificationPurpose.Registration, VerificationChannel.Sms, identifier, null, cancellationToken);
    }

    public async Task<Result<TraderRegistered>> RegisterAsync(RegisterTraderRequest request, CancellationToken cancellationToken)
    {
        if (RefusalOfChannel(request.Channel) is { } refused)
        {
            return refused;
        }

        var identifier = PhoneNumbers.Normalize(request.Identifier);
        if (identifier is null)
        {
            return AppError.Invalid(PhoneField);
        }

        if (!PasswordPolicy.IsValid(request.Password) || request.Password != request.PasswordConfirmation)
        {
            return AppError.Invalid("Password");
        }

        if (await accounts.FindByPhoneAsync(identifier, cancellationToken) is not null)
        {
            return AlreadyRegistered();
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
        var trader = new Account
        {
            SubjectType = SubjectType.Trader,
            StatusId = StatusIds.Account.Active,
            Username = await usernames.NewTraderUsernameAsync(cancellationToken),
            DisplayName = identifier,
            PhoneNumber = identifier,
            PhoneVerifiedAt = now,
            PasswordHash = passwordHasher.Hash(request.Password),
        };
        accounts.Add(trader);
        var (customer, linkedExisting) = await customerLinker.LinkNewTraderAsync(trader, cooperative.Id, cancellationToken);

        audit.Record(new AuditEntry("Account.Registered", nameof(Account))
        {
            RecordId = trader.Id,
            RecordCode = trader.Username,
            ActorAccountId = trader.Id,
            After = new { trader.Username, trader.SubjectType, trader.PhoneNumber, CustomerId = customer.Id, LinkedExistingCustomer = linkedExisting },
        });
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateRecordException)
        {
            // A parallel registration took the phone number between the check above and this save.
            return AlreadyRegistered();
        }

        // ACCOUNT-01 step 7: MSG-ACCOUNT-01 with the existing profile, MSG-ACCOUNT-02 with a new one (ACCOUNT-01.1).
        var notice = linkedExisting ? UserMessages.RegisteredWithExistingCustomerProfile : UserMessages.RegisteredWithNewCustomerProfile;
        return new TraderRegistered(trader.Id, trader.Username, linkedExisting, notice.Text, notice.Code);
    }

    private static readonly string PhoneField = ContactInputs.FieldName(VerificationChannel.Sms);

    /// <summary>The request keeps its <c>channel</c> field; only <c>Sms</c> is accepted, anything else names the field and says why.</summary>
    private static AppError? RefusalOfChannel(VerificationChannel channel) =>
        channel == VerificationChannel.Sms ? null : AppError.Invalid("Channel", "Registration uses a phone number.");

    /// <summary>MSG-ACCOUNT-03 naming the phone number, also as <c>field</c>.</summary>
    private static AppError AlreadyRegistered() => AppError.Conflict(
        UserMessages.AlreadyRegistered(ContactInputs.ContactName(VerificationChannel.Sms)),
        new Dictionary<string, object?> { ["field"] = PhoneField });
}
