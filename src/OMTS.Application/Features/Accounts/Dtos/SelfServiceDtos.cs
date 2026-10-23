using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="Channel">Sms (phone number) or Email.</param>
public sealed record RegisterTraderRequest(
    VerificationChannel Channel,
    string Identifier,
    string Password,
    string PasswordConfirmation,
    string VerificationToken);

/// <param name="OldDocumentsHidden">Several offline profiles share the number (MSG57); the Owner links them later.</param>
public sealed record TraderRegistered(Guid AccountId, string Username, bool OldDocumentsHidden, string? Notice);

public sealed record StartRecoveryRequest(string Identifier);

/// <summary>Always the same answer (MSG15), whether or not a code was sent.</summary>
public sealed record RecoveryStarted(Guid RequestId, string Message);

public sealed record ResetPasswordRequest(string VerificationToken, string NewPassword, string PasswordConfirmation);

/// <param name="CurrentPassword">Proof by the current password…</param>
/// <param name="VerificationToken">…or by an email code with purpose ChangePassword.</param>
public sealed record ChangePasswordRequest(
    string? CurrentPassword,
    string? VerificationToken,
    string NewPassword,
    string PasswordConfirmation,
    ClientType ClientType = ClientType.Web,
    string? DeviceName = null);

/// <param name="PhoneNumber">Required for traders; staff may omit it to confirm the number the Owner entered.</param>
public sealed record StartPhoneVerificationRequest(string? PhoneNumber);

public sealed record ConfirmContactRequest(string VerificationToken);

/// <param name="OldDocumentsHidden">Trader only: several offline profiles share the number (MSG57).</param>
public sealed record ContactConfirmed(string Contact, bool OldDocumentsHidden, string? Notice);

public sealed record UpdateProfileRequest(string DisplayName, string? RowVersion, string? Username = null);

/// <param name="CurrentPassword">Required: a stolen session alone must not be able to take over the account's contacts.</param>
public sealed record StartContactChangeRequest(VerificationChannel Channel, string Target, string? CurrentPassword = null);

public sealed record ProfileView(
    Guid AccountId,
    string Username,
    string DisplayName,
    SubjectType SubjectType,
    string? Email,
    bool EmailVerified,
    string? PhoneNumber,
    bool PhoneVerified,
    IReadOnlyList<ProfileGrantView> Grants,
    string RowVersion);

public sealed record ProfileGrantView(string Role, Guid? ZoneId, string? ZoneName, bool AllZones);
