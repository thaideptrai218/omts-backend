namespace OMTS.Domain.Enums;

/// <summary>Why a verification code was sent (UC01, UC03, UC04, UC05, UC08).</summary>
public enum VerificationPurpose
{
    /// <summary>Retired: a trader no longer registers with a code. The value stays because rows of it are stored; no new request has it and an old one is refused.</summary>
    Registration,
    AccountRecovery,
    VerifyPhone,
    VerifyEmail,
    ChangePhone,
    ChangeEmail,

    /// <summary>Retired: a password is changed with the current password or the temporary one, no longer with a code sent by email. The value stays because rows of it are stored; nothing creates one.</summary>
    ChangePassword,
}
