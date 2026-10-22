namespace OMTS.Application.Common.Messages;

/// <summary>
/// SRS message list (22/10/2026 wording). Every error response carries one of these, so web and mobile
/// show the same text for the same situation. Keep the wording identical to the SRS.
/// </summary>
public static class UserMessages
{
    public static UserMessage Invalid(string field) => new("MSG01", $"{field} is invalid. Please check and enter it again.");

    public static readonly UserMessage NoPermission = new("MSG02", "You do not have permission to perform this action.");

    public static UserMessage AlreadyExists(string item) => new(
        "MSG03", $"{item} already exists. The system opens the existing record for you to check and does not create a copy.");

    public static UserMessage ServiceNotResponding(string service) => new(
        "MSG04", $"The {service} service is not responding. The action has not been completed and the data in OMTS is unchanged; please try again later.");

    public static readonly UserMessage FileOperationFailed = new(
        "MSG05", "The file operation could not be completed. The source data has not changed; please try again.");

    public static UserMessage ConcurrentUpdate(string userName, string time) => new(
        "MSG06",
        $"The data was just updated by {userName} at {time}. Your changes have not been saved; please review the new version and then save again or cancel.");

    /// <summary>MSG06 when the other writer is not known (fallback of the API exception handler).</summary>
    public static readonly UserMessage ConcurrentUpdateByAnotherUser = new(
        "MSG06",
        "The data was just updated by another user. Your changes have not been saved; please review the new version and then save again or cancel.");

    public static UserMessage NoData(string objectName) => new("MSG08", $"There is no data for {objectName} in the selected scope.");

    public static readonly UserMessage ConfirmRoleEdit = new(
        "MSG09", "Are you sure you want to edit this role? This action is recorded in the audit log.");

    public static UserMessage Saved(string what) => new("MSG10", $"{what} saved.");

    public static UserMessage StillInUse(int count, string relatedList) => new(
        "MSG11", $"Cannot proceed because {count} records are still in use: {relatedList}. Please handle these records first.");

    public static readonly UserMessage LoginIncorrect = new("MSG12", "The login information is incorrect. Please check and try again.");

    public static readonly UserMessage AccountUnusable = new("MSG13", "This account cannot be used at the moment. Please contact the support channel.");

    public static readonly UserMessage CannotRegister = new(
        "MSG14", "Cannot register with this email or phone number. Please log in or recover your account.");

    public static readonly UserMessage RecoveryCodeMaybeSent = new(
        "MSG15", "If the information is valid, a verification code will be sent to the email or phone number you just entered.");

    public static readonly UserMessage WrongVerificationCode = new(
        "MSG16", "The verification code is incorrect or has expired. You can enter it again or request a new code.");

    public static readonly UserMessage WrongCurrentPassword = new(
        "MSG17", "The current password is incorrect. Your password has not been changed.");

    public static readonly UserMessage PasswordChanged = new("MSG18", "Your password has been changed.");

    public static readonly UserMessage SessionExpired = new("MSG19", "Your login session has expired. Please log in again.");

    public static readonly UserMessage UsernameReadOnly = new(
        "MSG20", "You cannot change your username yourself. Please contact the Cooperative Owner or the support channel.");

    public static UserMessage ChannelNotEligible(string channel) => new(
        "MSG21", $"The {channel} channel is not configured or not eligible, so it cannot be enabled yet.");

    public static readonly UserMessage PushDeclined = new(
        "MSG22", "The device has declined push notifications. Other valid channels are still saved.");

    public static readonly UserMessage MandatoryNotification = new(
        "MSG23", "This notification item is mandatory and cannot be turned off.");

    public static UserMessage AccountAlreadyLinked(string employeeName) => new(
        "MSG24", $"This account is already linked to the employee profile {employeeName}.");

    public static readonly UserMessage OldDocumentsHidden = new(
        "MSG57", "Old documents are temporarily hidden until identity verification is complete.");

    public static UserMessage CannotChangeStatus(string entity, string currentStatus, string newStatus, string reason) => new(
        "MSG61", $"Cannot change {entity} from “{currentStatus}” to “{newStatus}”: {reason}.");

    /// <param name="maskedTarget">e.g. "n***@mail.com".</param>
    /// <param name="contact">"email" or "phone number".</param>
    public static UserMessage CodeSentToNewContact(string maskedTarget, string contact) => new(
        "MSG62",
        $"A verification code has been sent to {maskedTarget}. Enter the code to finish; the old {contact} remains in use until verification is complete.");

    public static readonly UserMessage PhoneVerificationRequired = new(
        "MSG67",
        "Your account needs a verified phone number to continue using OMTS. Please enter or confirm your phone number and enter the verification code sent via SMS.");

    public static readonly UserMessage TemporaryPasswordExpired = new(
        "MSG68",
        "Your temporary password has expired. Staff accounts: please ask the Cooperative Owner to reset it. Cooperative Owner and trader accounts: please use Forgot password.");

    public static readonly UserMessage TooManyAttempts = new("MSG69", "You have exceeded the allowed number of attempts. Please try again in 15 minutes.");

    /// <summary>Not in the SRS list: the record does not exist (or is not visible to the caller).</summary>
    public static readonly UserMessage NotFound = new("NOT_FOUND", "The record does not exist.");

    /// <summary>Not in the SRS list: a verification code was requested too often (ACS-06 send limits).</summary>
    public static UserMessage TooManyCodeRequests(int waitSeconds) => new(
        "TOO_MANY_REQUESTS",
        waitSeconds > 0
            ? $"Please wait {waitSeconds} seconds before requesting a new code."
            : "No more codes can be sent for this request. Please start again.");

    /// <summary>Not in the SRS list: changing the password with an email code needs a verified email (ACS-04).</summary>
    public static readonly UserMessage NoVerifiedEmail = new(
        "NO_VERIFIED_EMAIL",
        "Your account has no verified email. Add and verify an email in your profile, or log out and use Forgot password with your verified phone number; staff accounts without a verified contact can ask the Cooperative Owner.");

    /// <summary>Not in the SRS list: an unexpected server error; details are only in the server log.</summary>
    public static readonly UserMessage UnexpectedError = new("UNEXPECTED_ERROR", "An unexpected error occurred. Please try again later.");

    /// <summary>Not in the SRS list: the temporary password must be changed before other functions (ACS-02).</summary>
    public static readonly UserMessage PasswordChangeRequired = new(
        "PASSWORD_CHANGE_REQUIRED", "Please change your temporary password before using other functions.");
}
