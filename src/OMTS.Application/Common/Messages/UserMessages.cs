namespace OMTS.Application.Common.Messages;

/// <summary>
/// SRS message list (22/10/2026 wording). Every error response carries one of these, so web and mobile
/// show the same text for the same situation. Keep the wording identical to the SRS.
/// </summary>
public static class UserMessages
{
    public static readonly UserMessage NoPermission = new("MSG02", "You do not have permission to perform this action.");

    public static readonly UserMessage FileOperationFailed = new(
        "MSG05", "The file operation could not be completed. The source data has not changed; please try again.");

    public static UserMessage ConcurrentUpdate(string userName, string time) => new(
        "MSG06",
        $"The data was just updated by {userName} at {time}. Your changes have not been saved; please review the new version and then save again or cancel.");

    /// <summary>MSG06 when the other writer is not known (fallback of the API exception handler).</summary>
    public static readonly UserMessage ConcurrentUpdateByAnotherUser = new(
        "MSG06",
        "The data was just updated by another user. Your changes have not been saved; please review the new version and then save again or cancel.");

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

    /// <summary>Not in the SRS list: an unexpected server error; details are only in the server log.</summary>
    public static readonly UserMessage UnexpectedError = new("UNEXPECTED_ERROR", "An unexpected error occurred. Please try again later.");

    /// <summary>Not in the SRS list: the temporary password must be changed before other functions (ACS-02).</summary>
    public static readonly UserMessage PasswordChangeRequired = new(
        "PASSWORD_CHANGE_REQUIRED", "Please change your temporary password before using other functions.");
}
