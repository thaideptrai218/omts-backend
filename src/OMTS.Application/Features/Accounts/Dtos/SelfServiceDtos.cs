using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="Channel">Must be Sms: a phone number is the only way to register.</param>
public sealed record RegisterTraderRequest(
    VerificationChannel Channel,
    string Identifier,
    string Password,
    string PasswordConfirmation,
    string VerificationToken);

/// <param name="LinkedExistingCustomer">The account was linked to the one customer profile with this phone number (BR-ACCOUNT-04).</param>
/// <param name="Notice">MSG-ACCOUNT-01 when <paramref name="LinkedExistingCustomer"/>, otherwise MSG-ACCOUNT-02.</param>
/// <param name="NoticeCode">The code of <paramref name="Notice"/>.</param>
public sealed record TraderRegistered(Guid AccountId, string Username, bool LinkedExistingCustomer, string Notice, string NoticeCode);

public sealed record StartRecoveryRequest(string Identifier);

/// <summary>Always the same answer (MSG-ACCOUNT-20), whether or not a code was sent.</summary>
public sealed record RecoveryStarted(Guid RequestId, string Message, string MessageCode);

public sealed record ResetPasswordRequest(string VerificationToken, string NewPassword, string PasswordConfirmation);

/// <param name="CurrentPassword">Proof of identity; not needed for the forced change after a temporary password (ACCOUNT-04.1).</param>
public sealed record ChangePasswordRequest(
    string? CurrentPassword,
    string NewPassword,
    string PasswordConfirmation,
    ClientType ClientType = ClientType.Web,
    string? DeviceName = null);

/// <param name="PhoneNumber">Required for traders; staff and the Owner may omit it to confirm the number on the account.</param>
public sealed record StartPhoneVerificationRequest(string? PhoneNumber);

public sealed record ConfirmContactRequest(string VerificationToken);

/// <param name="Notice">MSG-GEN-05 after a contact change; null after the phone step of the login.</param>
/// <param name="NoticeCode">The code of <paramref name="Notice"/>.</param>
public sealed record ContactConfirmed(string Contact, string? Notice, string? NoticeCode);

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

/// <param name="Role">Display name of the fixed role.</param>
/// <param name="RoleCode">ZONE_MANAGER or ACCOUNTANT.</param>
public sealed record ProfileGrantView(string Role, string RoleCode, Guid? ZoneId, string? ZoneName, bool AllZones);
