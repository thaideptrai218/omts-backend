namespace OMTS.Application.Common.Messages;

/// <summary>
/// SRS message list (22/10/2026 wording). Every error response carries one of these, so web and mobile
/// show the same text for the same situation. Keep the wording identical to the SRS.
/// </summary>
public static class UserMessages
{
    public static readonly UserMessage NoPermission = new("MSG02", "You do not have permission to perform this action.");

    public static readonly UserMessage LoginIncorrect = new("MSG12", "The login information is incorrect. Please check and try again.");

    public static readonly UserMessage AccountUnusable = new("MSG13", "This account cannot be used at the moment. Please contact the support channel.");

    public static readonly UserMessage SessionExpired = new("MSG19", "Your login session has expired. Please log in again.");

    public static readonly UserMessage PhoneVerificationRequired = new(
        "MSG67",
        "Your account needs a verified phone number to continue using OMTS. Please enter or confirm your phone number and enter the verification code sent via SMS.");

    public static readonly UserMessage TemporaryPasswordExpired = new(
        "MSG68",
        "Your temporary password has expired. Staff accounts: please ask the Cooperative Owner to reset it. Cooperative Owner and trader accounts: please use Forgot password.");

    public static readonly UserMessage TooManyAttempts = new("MSG69", "You have exceeded the allowed number of attempts. Please try again in 15 minutes.");

    /// <summary>Not in the SRS list: the temporary password must be changed before other functions (ACS-02).</summary>
    public static readonly UserMessage PasswordChangeRequired = new(
        "PASSWORD_CHANGE_REQUIRED", "Please change your temporary password before using other functions.");
}
