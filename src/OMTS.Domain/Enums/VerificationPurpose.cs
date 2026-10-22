namespace OMTS.Domain.Enums;

/// <summary>Why a verification code was sent (UC01, UC03, UC04, UC05, UC08).</summary>
public enum VerificationPurpose
{
    Registration,
    AccountRecovery,
    VerifyPhone,
    VerifyEmail,
    ChangePhone,
    ChangeEmail,
    ChangePassword,
}
