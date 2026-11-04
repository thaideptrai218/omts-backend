using OMTS.Domain.Entities.Identity;
using static OMTS.Application.Common.Messages.MessageCodes;

namespace OMTS.Application.Common.Messages;

/// <summary>
/// SRS message list (27/10/2026 catalogue, Part V §2). Every error response carries one of these, so web and mobile
/// show the same text for the same situation. Keep the wording identical to the SRS. Each message names the old
/// code of the same situation as <see cref="UserMessage.LegacyCode"/> (sent for one release); where the old code
/// depends on the call site, the caller sets it. The old codes that are still sent as the code are listed in
/// <see cref="Legacy.KeptAsCode"/> and grouped at the end.
/// </summary>
public static class UserMessages
{
    // ---- MSG-GEN ----

    /// <summary>MSG-GEN-01: a mandatory field is missing or a value is invalid.</summary>
    public static UserMessage Invalid(string field) => new(Gen01, $"{field} is missing or invalid. Please check and enter it again.", Legacy.Msg01);

    /// <summary>MSG-GEN-02: a code or record already exists; the entries are kept.</summary>
    public static UserMessage AlreadyExists(string item) => new(
        Gen02, $"{item} already exists. Your entries are kept: change them, or open the existing record.", Legacy.Msg03);

    /// <summary>MSG-GEN-03 (GFR-01): a file upload failed. Old code MSG05 (or MSG04 where the caller says so).</summary>
    public static readonly UserMessage UploadFailed = new(
        Gen03, "The file could not be uploaded. Your other entries are kept; please try again.", Legacy.Msg05);

    /// <summary>MSG-GEN-05: a record is saved.</summary>
    public static UserMessage Saved(string what) => new(Gen05, $"{what} saved.", Legacy.Msg10);

    /// <summary>MSG-GEN-06: blocked because related records are still in use or a cycle is open.</summary>
    public static UserMessage StillInUse(int count, string relatedList) => new(
        Gen06, $"Cannot proceed because {count} records are still in use: {relatedList}. Please handle these records first.", Legacy.Msg11);

    /// <summary>MSG-GEN-08 (GFR-02): the role or zone access does not allow the function; nothing is changed.</summary>
    public static readonly UserMessage NoPermission = new(
        Gen08, "You do not have permission to perform this action. Nothing has been changed.", Legacy.Msg02);

    /// <summary>MSG-GEN-09 (GBR-01): a record in this status cannot be used for the change asked for.</summary>
    /// <param name="objectName">e.g. "Farming zone"; the first letter is capitalized.</param>
    /// <param name="status">The display name of the current status, e.g. "Discontinued".</param>
    public static UserMessage WrongStatus(string objectName, string status) => new(
        Gen09, $"{Capitalized(objectName)} is {status} and cannot be used for this action.", Legacy.Msg61);

    /// <summary>MSG-GEN-10: a list has no data for the filters chosen.</summary>
    /// <param name="filterDescription">e.g. "period and filters".</param>
    public static UserMessage NoDataForFilters(string filterDescription) => new(
        Gen10, $"There is no data for the {filterDescription} you chose. Please choose again.", Legacy.Msg08);

    // ---- MSG-WATER ----

    /// <summary>MSG-WATER-01 (WATER-01.2): the OCR Service read nothing, or did not answer.</summary>
    /// <param name="indicatorList">The values not read, e.g. "pH and salinity".</param>
    public static UserMessage OcrValuesNotRead(string indicatorList) => new(
        Water01, $"OMTS could not read {indicatorList} from the photo. Please look at the meter and type in the missing values.", Legacy.Msg04);

    /// <summary>MSG-WATER-05: the minimum of an indicator is not less than its maximum.</summary>
    /// <param name="indicator">e.g. "pH", or "pH and salinity" when both are wrong.</param>
    public static UserMessage MinimumNotLessThanMaximum(string indicator) => new(
        Water05, $"The minimum of {indicator} must be less than the maximum. The thresholds were not saved. Please correct the values.", Legacy.Msg28);

    // ---- MSG-ACCOUNT ----

    /// <summary>MSG-ACCOUNT-02: the trader account got a new customer profile (several offline profiles share the number).</summary>
    public static readonly UserMessage RegisteredWithNewCustomerProfile = new(
        Account02,
        "Your account has been created. Invoices from before today will appear in your account after the cooperative adds them. Please log in.",
        Legacy.Msg57);

    /// <summary>MSG-ACCOUNT-03: the phone number or email entered at registration already belongs to an account.</summary>
    /// <param name="fieldName">"phone number" or "email".</param>
    public static UserMessage AlreadyRegistered(string fieldName) => new(
        Account03, $"This {fieldName} is already registered. Please log in, or select Forgot password if you have forgotten your password.", Legacy.Msg14);

    /// <summary>MSG-ACCOUNT-04 (BR-ACCOUNT-03).</summary>
    public static readonly UserMessage PhoneVerificationRequired = new(
        Account04,
        "Your account needs a verified phone number before you can use OMTS. Please enter or confirm your phone number, then enter the verification code sent to it by SMS.",
        Legacy.Msg67);

    /// <summary>MSG-ACCOUNT-05: wrong login information; it does not say which entry is wrong.</summary>
    public static readonly UserMessage LoginIncorrect = new(Account05, "The login information is incorrect. Please check it and try again.", Legacy.Msg12);

    /// <summary>
    /// MSG-ACCOUNT-06: login is paused for <see cref="Account.TemporaryLockDuration"/> after too many failed
    /// attempts. The retry time is the length of the pause, the same for every identifier (BR-ACCOUNT-05).
    /// </summary>
    public static readonly UserMessage TooManyLoginAttempts = new(
        Account06, $"Too many failed login attempts. Please try again after {Duration(Account.TemporaryLockDuration)}.", Legacy.Msg69);

    /// <summary>MSG-ACCOUNT-07: the account is Locked or has no effective zone–role pair.</summary>
    public static readonly UserMessage AccountUnusable = new(
        Account07, "This account cannot be used at the moment. Please contact the Cooperative Owner.", Legacy.Msg13);

    /// <summary>MSG-ACCOUNT-08 (BR-ACCOUNT-06).</summary>
    public static readonly UserMessage TemporaryPasswordExpired = new(
        Account08, "Your temporary password has expired. Please ask the Cooperative Owner for a new one.", Legacy.Msg68);

    /// <summary>MSG-ACCOUNT-09 (BR-ACCOUNT-02): the username, phone number or email belongs to another account.</summary>
    /// <param name="fieldName">"username", "phone number" or "email".</param>
    /// <param name="legacyCode">MSG03 (staff accounts) or MSG01 (own profile, phone verification).</param>
    public static UserMessage UsedByAnotherAccount(string fieldName, string legacyCode) => new(
        Account09, $"This {fieldName} is already used by another account. Please enter another one.", legacyCode);

    /// <summary>MSG-ACCOUNT-10: the password is recovered (ACCOUNT-03).</summary>
    public static readonly UserMessage PasswordRecovered = new(
        Account10, "Your password has been changed. Please log in with your new password.", Legacy.Msg18);

    /// <summary>MSG-ACCOUNT-11: the password is changed; the other devices are logged out (ACCOUNT-04).</summary>
    public static readonly UserMessage PasswordChanged = new(
        Account11, "Your password has been changed. You have been logged out on your other devices.", Legacy.Msg18);

    /// <summary>MSG-ACCOUNT-13 (GFR-06): an SMS or email the user waits for could not be sent.</summary>
    /// <param name="item">e.g. "verification code".</param>
    /// <param name="channel">"SMS" or "email".</param>
    public static UserMessage CouldNotSend(string item, string channel) => new(
        Account13, $"The {item} could not be sent by {channel}. Nothing has been changed. Please try again later.", Legacy.Msg04);

    /// <summary>MSG-ACCOUNT-14.</summary>
    public static readonly UserMessage WrongCurrentPassword = new(
        Account14, "The current password is incorrect. Your password has not been changed.", Legacy.Msg17);

    /// <summary>MSG-ACCOUNT-15 (ACCOUNT-04.2).</summary>
    public static readonly UserMessage NoVerifiedEmail = new(
        Account15,
        "Your account has no verified email, so no code can be sent. Use Forgot password with your phone number, or add an email in your personal profile.",
        Legacy.NoVerifiedEmail);

    /// <summary>MSG-ACCOUNT-16: a verification code is wrong or has expired.</summary>
    public static readonly UserMessage WrongVerificationCode = new(
        Account16, "The verification code is wrong or has expired. Please check it, or select Resend code to get a new one.", Legacy.Msg16);

    /// <summary>MSG-ACCOUNT-17: a code was entered wrongly too many times and is cancelled.</summary>
    public static readonly UserMessage TooManyWrongCodes = new(
        Account17, "You have entered a wrong code too many times. Please start again to get a new code.", Legacy.Msg69);

    /// <summary>MSG-ACCOUNT-18: too many codes were asked for (BR-ACCOUNT-09).</summary>
    /// <param name="waitSeconds">0 when this request has no send left: a new request must be started.</param>
    public static UserMessage TooManyCodeRequests(int waitSeconds) => new(
        Account18,
        waitSeconds > 0
            ? $"You have asked for too many codes. Please try again after {Duration(TimeSpan.FromSeconds(waitSeconds))}."
            : "You have asked for too many codes for this request. Please start again to get a new code.",
        Legacy.TooManyRequests);

    /// <summary>MSG-ACCOUNT-19: a verification code has been sent to the partly hidden address.</summary>
    /// <param name="maskedAddress">e.g. "n***@mail.com".</param>
    public static UserMessage CodeSent(string maskedAddress, int minutes) => new(
        Account19, $"A verification code has been sent to {maskedAddress}. It expires in {minutes} minutes.", Legacy.Msg62);

    /// <summary>MSG-ACCOUNT-20 (BR-ACCOUNT-07): the same answer whatever is entered.</summary>
    public static readonly UserMessage RecoveryCodeMaybeSent = new(
        Account20, "If this phone number or email belongs to an account, a verification code has been sent to it.", Legacy.Msg15);

    /// <summary>MSG-ACCOUNT-21 (ACCOUNT-07.1): push asked for, but no device of the account allows it.</summary>
    public static readonly UserMessage PushNotAllowedOnDevice = new(
        Account21,
        "Your preferences are saved. This device does not allow notifications from OMTS. To get them on this device, please turn them on in the device settings. You can still see all notifications in the notification list.",
        Legacy.Msg22);

    // ---- MSG-PURCHASE ----

    /// <summary>MSG-PURCHASE-03 (PURCHASE-02.0.E1): a quantity to return is not greater than 0 or above what is left.</summary>
    /// <param name="legacyCode">MSG59 (not greater than 0) or MSG45 (above the limit).</param>
    public static UserMessage ReturnQuantityOutsideLimit(string itemName, string maxQuantity, string unit, string limitExplanation, string legacyCode) => new(
        Purchase03, $"{itemName}: enter a quantity greater than 0 and at most {maxQuantity} {unit}. {limitExplanation}".TrimEnd(), legacyCode);

    /// <summary>MSG-PURCHASE-09 (PURCHASE-06.0.E1): a seed lot was seeded or the receipt has a purchase return.</summary>
    public static UserMessage ReceiptCannotBeCancelled(string receiptCode, string cause) => new(
        Purchase09, $"Receipt {receiptCode} cannot be cancelled: {cause}. The Cooperative Owner can correct it instead.", Legacy.Msg61);

    // ---- MSG-LABOR ----

    /// <summary>MSG-LABOR-03: the phone number belongs to another employee.</summary>
    public static UserMessage PhoneBelongsToEmployee(string employeeName) => new(
        Labor03,
        $"This phone number belongs to {employeeName}. Each employee needs their own phone number for the employee app: enter another number, or leave it empty.",
        Legacy.Msg03);

    // ---- Old codes still sent as the code (Legacy.KeptAsCode) ----

    /// <summary>MSG05: an export file could not be generated, or a stored file could not be read.</summary>
    public static readonly UserMessage FileOperationFailed = new(
        Legacy.Msg05, "The file operation could not be completed. The source data has not changed; please try again.");

    /// <summary>MSG06 (NFR-09).</summary>
    public static UserMessage ConcurrentUpdate(string userName, string time) => new(
        Legacy.Msg06,
        $"The data was just updated by {userName} at {time}. Your changes have not been saved; please review the new version and then save again or cancel.");

    /// <summary>MSG06 when the other writer is not known (fallback of the API exception handler).</summary>
    public static readonly UserMessage ConcurrentUpdateByAnotherUser = new(
        Legacy.Msg06,
        "The data was just updated by another user. Your changes have not been saved; please review the new version and then save again or cancel.");

    public static readonly UserMessage SessionExpired = new(Legacy.Msg19, "Your login session has expired. Please log in again.");

    public static UserMessage ChannelNotEligible(string channel) => new(
        Legacy.Msg21, $"The {channel} channel is not configured or not eligible, so it cannot be enabled yet.");

    public static readonly UserMessage MandatoryNotification = new(
        Legacy.Msg23, "This notification item is mandatory and cannot be turned off.");

    public static UserMessage AccountAlreadyLinked(string employeeName) => new(
        Legacy.Msg24, $"This account is already linked to the employee profile {employeeName}.");

    /// <summary>Notification body (replaced by MSG-FARM-01/02 with the notifications rework).</summary>
    public static UserMessage LicenseExpiry(string documentName, string zoneNames, int days) => new(
        Legacy.Msg25, $"Document {documentName} of zone {zoneNames} expires in {days} days or has expired.");

    /// <summary>Notification body (replaced by MSG-WATER-06 with the notifications rework).</summary>
    public static UserMessage WaterMeasurementOverdue(string zoneName) => new(
        Legacy.Msg26, $"Zone {zoneName} has had no confirmed water measurement for more than 1 month. Please measure and record it.");

    /// <summary>Notification body (replaced by MSG-WATER-03 with the notifications rework).</summary>
    /// <param name="metricNames">The violated indicators joined by ", ", e.g. "pH, Salinity".</param>
    public static UserMessage WaterThresholdExceeded(string metricNames, string zoneName) => new(
        Legacy.Msg27, $"{metricNames} at zone {zoneName} exceeds the set threshold.");

    public static readonly UserMessage NotThousands = new(Legacy.Msg41, "The amount must be a multiple of 1,000 VND.");

    public static UserMessage ExceedsLimit(string field, string limit) => new(Legacy.Msg45, $"{field} exceeds the allowed limit ({limit}).");

    public static readonly UserMessage OldDocumentsHidden = new(
        Legacy.Msg57, "Old documents are temporarily hidden until identity verification is complete.");

    /// <summary>MSG58 (NFR-13).</summary>
    public static UserMessage SomeoneIsEditing(string userName, string time) => new(
        Legacy.Msg58,
        $"{userName} has been editing this record since {time}. You can wait or edit anyway; if you save later, you will be shown the new version before saving.");

    /// <summary>Technical: the record does not exist (or is not visible to the caller).</summary>
    public static readonly UserMessage NotFound = new(Legacy.NotFound, "The record does not exist.");

    /// <summary>Technical: one client sent too many requests to an anonymous endpoint (per-IP limit).</summary>
    public static readonly UserMessage TooManyRequests = new(
        Legacy.TooManyRequests, "Too many requests from this device. Please wait a minute and try again.");

    /// <summary>Technical: an unexpected server error; details are only in the server log.</summary>
    public static readonly UserMessage UnexpectedError = new(Legacy.UnexpectedError, "An unexpected error occurred. Please try again later.");

    /// <summary>Technical: the temporary password must be changed before other functions (ACCOUNT-02.2).</summary>
    public static readonly UserMessage PasswordChangeRequired = new(
        Legacy.PasswordChangeRequired, "Please change your temporary password before using other functions.");

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>"15 minutes", "1 minute", "45 seconds": whole minutes from one minute up (rounded up).</summary>
    private static string Duration(TimeSpan span)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));
        if (seconds < 60)
        {
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        var minutes = (seconds + 59) / 60;
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
