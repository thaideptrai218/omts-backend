using System.Globalization;
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

    // ---- MSG-FARM ----

    /// <summary>MSG-FARM-01 (GFR-03): notification to the Owner when a zone license becomes Expiring soon.</summary>
    /// <param name="documentType">Optional license type code (stored), e.g. AQUACULTURE_LICENSE.</param>
    /// <param name="documentTypeName">Its English name, for the text ("Zone license" when there is no type).</param>
    /// <param name="zoneCode">Code of the zone of the license.</param>
    public static UserMessage LicenseExpiringSoon(
        string? documentType, string? documentTypeName, string documentNumber, string zoneCode, DateOnly expiryDate, int days) => new(
        Farm01,
        $"{LicenseDocument(documentTypeName, documentNumber, zoneCode)} expires on {Date(expiryDate)}, in {days} days. Please renew it and record the new one.",
        Legacy.Msg25)
    {
        Parameters = LicenseParameters(documentType, documentNumber, zoneCode, expiryDate, days),
    };

    /// <summary>MSG-FARM-02 (GFR-03): notification to the Owner when a zone license becomes Expired.</summary>
    public static UserMessage LicenseExpired(
        string? documentType, string? documentTypeName, string documentNumber, string zoneCode, DateOnly expiryDate) => new(
        Farm02,
        $"{LicenseDocument(documentTypeName, documentNumber, zoneCode)} expired on {Date(expiryDate)}. Please renew it and record the new one.",
        Legacy.Msg25)
    {
        Parameters = LicenseParameters(documentType, documentNumber, zoneCode, expiryDate, days: null),
    };

    /// <summary>MSG-FARM-01 (GFR-03): notification to the Owner when a cooperative certification becomes Expiring soon.</summary>
    /// <param name="number">The certification number; null when it has none.</param>
    public static UserMessage CertificationExpiringSoon(string name, string? number, DateOnly expiryDate, int days) => new(
        Farm01,
        $"{CertificationDocument(name, number)} expires on {Date(expiryDate)}, in {days} days. Please renew it and record the new one.",
        Legacy.Msg25)
    {
        Parameters = CertificationParameters(name, number, expiryDate, days),
    };

    /// <summary>MSG-FARM-02 (GFR-03): notification to the Owner when a cooperative certification becomes Expired.</summary>
    public static UserMessage CertificationExpired(string name, string? number, DateOnly expiryDate) => new(
        Farm02,
        $"{CertificationDocument(name, number)} expired on {Date(expiryDate)}. Please renew it and record the new one.",
        Legacy.Msg25)
    {
        Parameters = CertificationParameters(name, number, expiryDate, days: null),
    };
    // ---- MSG-DEBT ----

    /// <summary>MSG-DEBT-01 (DEBT-01 step 5): asks to use the account, after the Payment Notification Service accepted it.</summary>
    /// <param name="oldCodesText">{old_codes_text}: empty, or <see cref="OldVietQrCodesText"/>.</param>
    public static UserMessage UseReceivingBankAccount(string oldCodesText, string accountNumber, string bankName, string accountHolder) => new(
        Debt01, $"{oldCodesText}Use account {accountNumber} at {bankName} ({accountHolder}) for the VietQR codes of the sales invoices?");

    /// <summary>
    /// {old_codes_text} of MSG-DEBT-01 when an account was already set (BR-DEBT-01). The SRS gives the placeholder but no
    /// wording; this is the rule of BR-DEBT-01 in a sentence.
    /// </summary>
    public static string OldVietQrCodesText(int count) => count == 1
        ? "1 unpaid invoice has a VietQR code already sent for the old account. A transfer made with it is not recorded automatically. "
        : $"{count} unpaid invoices have VietQR codes already sent for the old account. A transfer made with them is not recorded automatically. ";

    /// <summary>MSG-DEBT-02 (DEBT-01.0.E2): the Payment Notification Service does not accept the account or does not answer.</summary>
    public static UserMessage ReceivingBankAccountNotAccepted(string accountNumber, string bankName) => new(
        Debt02,
        $"The payment notification service did not accept account {accountNumber} at {bankName}. The receiving bank account has not been changed. Please check the account, or try again later.");

    // ---- MSG-WATER ----

    /// <summary>MSG-WATER-01 (WATER-01.2): the OCR Service could not read one or more values, or did not answer.</summary>
    /// <param name="indicatorList">The values not read, e.g. "pH and salinity".</param>
    public static UserMessage OcrValuesNotRead(string indicatorList) => new(
        Water01, $"OMTS could not read {indicatorList} from the photo. Please look at the meter and type in the missing values.", Legacy.Msg04);

    /// <summary>MSG-WATER-02 (WATER-01 step 8): before saving, a value is outside the water thresholds of the zone.</summary>
    /// <param name="valuesOutside">Each value outside with its allowed range.</param>
    public static UserMessage WaterValuesOutsideBeforeSave(IReadOnlyList<WaterValueOutside> valuesOutside) => new(
        Water02, $"{string.Join("; ", valuesOutside.Select(v => v.Describe()))}. Please check the meter again before you save.");

    /// <summary>MSG-WATER-03 (BR-WATER-05): the water alert notification.</summary>
    /// <param name="corrected">The alert of a corrected measurement: "(corrected measurement)" in the text.</param>
    /// <param name="raftCode">The raft where the water was measured.</param>
    /// <param name="measuredAt">Shown in Vietnam time.</param>
    /// <param name="valuesOutside">Each value outside with its allowed range.</param>
    public static UserMessage WaterAlert(
        bool corrected, string zoneName, string raftCode, DateTimeOffset measuredAt, IReadOnlyList<WaterValueOutside> valuesOutside) => new(
        Water03,
        $"Water alert{(corrected ? " (corrected measurement)" : "")}: zone {zoneName}, raft {raftCode}, measured on {BusinessClock.Format(measuredAt)}. "
            + $"{string.Join("; ", valuesOutside.Select(v => v.Describe()))}. Please check the water.",
        Legacy.Msg27)
    {
        Parameters = new Dictionary<string, object?>
        {
            ["corrected"] = corrected,
            ["zone_name"] = zoneName,
            ["raft_code"] = raftCode,
            ["measurement_time"] = measuredAt.ToOffset(BusinessClock.UtcOffset),
            ["values_outside"] = valuesOutside,
        },
    };

    /// <summary>MSG-WATER-04 (BR-WATER-05): an earlier water alert was withdrawn after a correction or a void.</summary>
    /// <param name="measuredAt">The time of the alert being withdrawn, shown in Vietnam time.</param>
    /// <param name="correctedOrVoided">"corrected" or "voided".</param>
    public static UserMessage WaterAlertWithdrawn(string zoneName, DateTimeOffset measuredAt, string userName, string correctedOrVoided, string reason) => new(
        Water04,
        $"The water alert of zone {zoneName} (measured on {BusinessClock.Format(measuredAt)}) was withdrawn: {userName} {correctedOrVoided} the measurement. Reason: {reason}.")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["zone_name"] = zoneName,
            ["measurement_time"] = measuredAt.ToOffset(BusinessClock.UtcOffset),
            ["user_name"] = userName,
            ["corrected_or_voided"] = correctedOrVoided,
            ["reason"] = reason,
        },
    };

    /// <summary>MSG-WATER-05: the minimum of an indicator is not less than its maximum.</summary>
    /// <param name="indicator">e.g. "pH", or "pH and salinity" when both are wrong.</param>
    public static UserMessage MinimumNotLessThanMaximum(string indicator) => new(
        Water05, $"The minimum of {indicator} must be less than the maximum. The thresholds were not saved. Please correct the values.", Legacy.Msg28);

    /// <summary>MSG-WATER-06 (GFR-04): reminder to measure the water of a zone.</summary>
    /// <param name="lastDate">The business day of the latest measurement, or the start of the overdue period when there is none.</param>
    public static UserMessage WaterMeasurementOverdue(string zoneName, DateOnly lastDate) => new(
        Water06, $"Zone {zoneName} has had no water measurement since {Date(lastDate)}. Please measure the water and record the result in OMTS.", Legacy.Msg26)
    {
        Parameters = new Dictionary<string, object?> { ["zone_name"] = zoneName, ["last_date"] = lastDate },
    };

    // ---- MSG-ACCOUNT ----

    /// <summary>MSG-ACCOUNT-01: the trader account is linked to the existing customer profile (ACCOUNT-01 step 7).</summary>
    public static readonly UserMessage RegisteredWithExistingCustomerProfile = new(
        Account01, "Your account has been created. Please log in with your phone number or email and your password.");

    /// <summary>MSG-ACCOUNT-02: the trader account got a new customer profile, as no profile or several matched (ACCOUNT-01.1).</summary>
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
    /// attempts (BR-ACCOUNT-05).
    /// </summary>
    /// <param name="waitSeconds">What is left of the pause: {retry_time} in the text.</param>
    public static UserMessage TooManyLoginAttempts(int waitSeconds) => new(
        Account06, $"Too many failed login attempts. Please try again after {Duration(TimeSpan.FromSeconds(waitSeconds))}.", Legacy.Msg69);

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
        Account17, "You have entered a wrong code too many times. Please start again to get a new code.", Legacy.Msg16);

    /// <summary>MSG-ACCOUNT-18: too many codes were asked for (BR-ACCOUNT-09).</summary>
    /// <param name="waitSeconds">The real wait until a new code can be sent: {retry_time} in the text.</param>
    public static UserMessage TooManyCodeRequests(int waitSeconds) => new(
        Account18,
        $"You have asked for too many codes. Please try again after {Duration(TimeSpan.FromSeconds(waitSeconds))}.",
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

    /// <summary>MSG-ACCOUNT-22 (ACCOUNT-08.0.E1, ACCOUNT-10.1.E1): the record of a notification or of an audit event no longer exists.</summary>
    /// <param name="objectName">e.g. "Zone license"; the first letter is capitalized.</param>
    /// <param name="recordCode">The code the notification or the event shows for the record; may be empty.</param>
    public static UserMessage RecordNoLongerExists(string objectName, string recordCode) => new(
        Account22, $"{string.Join(' ', new[] { Capitalized(objectName), recordCode }.Where(p => p.Length > 0))} no longer exists.");

    // ---- MSG-PURCHASE ----

    /// <summary>MSG-PURCHASE-01 (BR-PURCHASE-05): notification to the Owner when a purchase receipt is completed.</summary>
    /// <param name="totalAmount">The total of the receipt in whole VND: the sum of its rounded lines (BR-PURCHASE-02).</param>
    public static UserMessage ReceiptCompleted(string userName, string receiptCode, string supplierName, int lineCount, decimal totalAmount) => new(
        Purchase01, $"{userName} completed purchase receipt {receiptCode} from {supplierName}: {lineCount} lines, {Vnd(totalAmount)} VND.")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["user_name"] = userName,
            ["receipt_code"] = receiptCode,
            ["supplier_name"] = supplierName,
            ["line_count"] = lineCount,
            ["total_amount"] = totalAmount,
        },
    };

    /// <summary>MSG-PURCHASE-03 (PURCHASE-02.0.E1): a quantity to return is not greater than 0 or above what is left.</summary>
    /// <param name="legacyCode">MSG59 (not greater than 0) or MSG45 (above the limit).</param>
    public static UserMessage ReturnQuantityOutsideLimit(string itemName, string maxQuantity, string unit, string limitExplanation, string legacyCode) => new(
        Purchase03, $"{itemName}: enter a quantity greater than 0 and at most {maxQuantity} {unit}. {limitExplanation}".TrimEnd(), legacyCode);

    /// <summary>MSG-PURCHASE-04 (BR-PURCHASE-05): notification to the Owner of a payment, money paid back or a cancelled payment; always pushed.</summary>
    /// <param name="transaction">The English {transaction_text}: what was recorded, up to the code ("the cancellation of payment"), from which the text is built.</param>
    /// <param name="transactionKind">The atomic parameter clients word it from: Payment, PaidBack, PaymentCancelled or PaidBackCancelled.</param>
    /// <param name="preposition">"to" or "by" the supplier.</param>
    /// <param name="debtAmount">The supplier debt after it; below zero when paid in advance.</param>
    public static UserMessage SupplierTransaction(
        string userName, string transaction, string transactionKind, string paymentCode, decimal amount, string preposition, string supplierName, decimal debtAmount)
    {
        var debtText = debtAmount < 0
            ? $"The cooperative has now paid {supplierName} {Vnd(-debtAmount)} VND in advance."
            : $"The cooperative now owes {supplierName} {Vnd(debtAmount)} VND.";
        return new UserMessage(Purchase04, $"{userName} recorded {transaction} {paymentCode} of {Vnd(amount)} VND {preposition} {supplierName}. {debtText}")
        {
            Parameters = new Dictionary<string, object?>
            {
                ["user_name"] = userName,
                ["transaction_kind"] = transactionKind,
                ["payment_code"] = paymentCode,
                ["amount"] = amount,
                ["supplier_name"] = supplierName,
                ["debt_amount"] = debtAmount,
            },
        };
    }

    /// <summary>MSG-PURCHASE-05 (PURCHASE-05.1): the payment is larger than the debt, or the debt is zero or below; a confirmation.</summary>
    /// <param name="debtAmount">What the cooperative owes, 0 when it has already paid in advance (the exact debt goes in the details of the answer).</param>
    /// <param name="advanceAmount">What it will have paid in advance after this payment (positive).</param>
    public static UserMessage PaymentAboveDebt(string supplierName, decimal debtAmount, decimal advanceAmount) => new(
        Purchase05,
        $"The cooperative owes {supplierName} {Vnd(debtAmount)} VND. After this payment, it will have paid {Vnd(advanceAmount)} VND in advance. Do you want to save the payment?");

    /// <summary>MSG-PURCHASE-06 (PURCHASE-05.4): a payment that is not cancelled has the same bank reference; a confirmation.</summary>
    public static UserMessage BankReferenceAlreadyRecorded(string paymentCode, decimal amount, DateOnly paymentDate, string bankReference) => new(
        Purchase06,
        $"Payment {paymentCode} of {Vnd(amount)} VND on {Date(paymentDate)} already has bank reference {bankReference}. Do you want to save this payment as well?");

    /// <summary>MSG-PURCHASE-07 (PURCHASE-06 step 2): cancelling the receipt would bring the supplier debt below zero.</summary>
    /// <param name="advanceAmount">What the cooperative will then have paid in advance (a positive amount).</param>
    public static UserMessage PaidInAdvanceAfterCancel(string supplierName, decimal advanceAmount) => new(
        Purchase07,
        $"After this cancellation, the cooperative will have paid {supplierName} {Vnd(advanceAmount)} VND in advance. The payments already recorded are not cancelled.")
    {
        Parameters = new Dictionary<string, object?> { ["supplier_name"] = supplierName, ["advance_amount"] = advanceAmount },
    };

    /// <summary>MSG-PURCHASE-08 (PURCHASE-06 step 7): notification to the Accountant and the Owner of a debt below zero after a cancellation.</summary>
    public static UserMessage ReceiptCancelledPaidInAdvance(string userName, string receiptCode, string supplierName, decimal advanceAmount) => new(
        Purchase08,
        $"{userName} cancelled purchase receipt {receiptCode} from {supplierName}. The cooperative has now paid {supplierName} {Vnd(advanceAmount)} VND in advance.")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["user_name"] = userName,
            ["receipt_code"] = receiptCode,
            ["supplier_name"] = supplierName,
            ["advance_amount"] = advanceAmount,
        },
    };

    /// <summary>MSG-PURCHASE-09 (PURCHASE-06.0.E1): a seed lot was seeded or the receipt has a purchase return.</summary>
    public static UserMessage ReceiptCannotBeCancelled(string receiptCode, string cause) => new(
        Purchase09, $"Receipt {receiptCode} cannot be cancelled: {cause}. The Cooperative Owner can correct it instead.", Legacy.Msg61);

    /// <summary>MSG-PURCHASE-10 (PURCHASE-06.2.E1): a corrected quantity is below the quantity seeded plus the quantity returned.</summary>
    public static UserMessage QuantityBelowUsed(string itemName, decimal minQuantity, string unit, decimal seededQuantity, decimal returnedQuantity) => new(
        Purchase10,
        $"{itemName}: the quantity cannot be less than {Quantity(minQuantity)} {unit} ({Quantity(seededQuantity)} seeded, {Quantity(returnedQuantity)} returned).");

    // ---- MSG-LABOR ----

    /// <summary>MSG-LABOR-01 (LABOR-01 step 5): asked of a Zone Manager adding an employee; a request without the answer is refused.</summary>
    public static UserMessage OwnerApprovalNeeded(string employeeName) => new(
        Labor01, $"Has the Cooperative Owner approved, in person, adding {employeeName} as a new employee?")
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName },
    };

    /// <summary>MSG-LABOR-02 (LABOR-01.3 step 5): reminder to the Owner when a staff account has the phone number of an employee who left.</summary>
    public static UserMessage LockStaffAccountReminder(string employeeName, DateOnly lastWorkingDay, string username) => new(
        Labor02,
        $"{employeeName} no longer works for the cooperative after {Date(lastWorkingDay)}. Staff account {username} has the same phone number: lock it in User accounts if it is no longer needed.")
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName, ["last_working_day"] = lastWorkingDay, ["username"] = username },
    };

    /// <summary>MSG-LABOR-03: the phone number belongs to another employee.</summary>
    public static UserMessage PhoneBelongsToEmployee(string employeeName) => new(
        Labor03,
        $"This phone number belongs to {employeeName}. Each employee needs their own phone number for the employee app: enter another number, or leave it empty.",
        Legacy.Msg03);

    // ---- MSG-SALE ----

    /// <summary>MSG-GEN-11 (SALE-10.0.E1): a list the user does not filter has nothing to show yet.</summary>
    public static readonly UserMessage NothingToShowYet = new(Gen11, "There is nothing to show yet.");

    /// <summary>MSG-SALE-01 (BR-SALE-03): a new purchase request, to the Cooperative Owner; a mandatory type.</summary>
    public static UserMessage PurchaseRequestNew(string customerName, decimal requestedKg, string productName, DateOnly pickupDate, string? note) => new(
        Sale01,
        $"{customerName} asks to buy about {Kg(requestedKg)} kg of {productName}, pickup on {Date(pickupDate)}. {(note is null ? "" : note + " ")}Please ask the Zone Manager to check the oysters, then respond.")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["customer_name"] = customerName,
            ["requested_kg"] = KgValue(requestedKg),
            ["product_name"] = productName,
            ["pickup_date"] = pickupDate,
            ["note"] = note,
        },
    };

    /// <summary>MSG-SALE-02 (BR-SALE-03): a trader cancelled a purchase request, to the Cooperative Owner.</summary>
    /// <param name="requestDate">The business day the request was sent.</param>
    public static UserMessage PurchaseRequestCancelled(string customerName, DateOnly requestDate, decimal requestedKg, string productName, string? reason) => new(
        Sale02,
        $"{customerName} cancelled the purchase request of {Date(requestDate)} for about {Kg(requestedKg)} kg of {productName}.{(reason is null ? "" : " " + reason)}")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["customer_name"] = customerName,
            ["request_date"] = requestDate,
            ["requested_kg"] = KgValue(requestedKg),
            ["product_name"] = productName,
            ["reason"] = reason,
        },
    };

    /// <summary>MSG-SALE-03 (BR-SALE-03): the response "accepted", to the trader account and the phone number of the profile.</summary>
    public static UserMessage PurchaseRequestAccepted(
        string cooperativeShortName, decimal requestedKg, string productName, decimal acceptedKg, DateOnly pickupDate, bool payLater, string? note) => new(
        Sale03,
        $"{cooperativeShortName}: your request for {Kg(requestedKg)} kg of {productName} is accepted for {Kg(acceptedKg)} kg, pickup on {Date(pickupDate)}{(payLater ? "; you may pay later" : "")}.{(note is null ? "" : " " + note)}")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["cooperative_short_name"] = cooperativeShortName,
            ["requested_kg"] = KgValue(requestedKg),
            ["product_name"] = productName,
            ["accepted"] = true,
            ["accepted_kg"] = KgValue(acceptedKg),
            ["pickup_date"] = pickupDate,
            ["pay_later"] = payLater,
            ["note"] = note,
        },
    };

    /// <summary>MSG-SALE-03 (BR-SALE-03): the response "declined", with the note that gives the reason.</summary>
    public static UserMessage PurchaseRequestDeclined(string cooperativeShortName, decimal requestedKg, string productName, string note) => new(
        Sale03,
        $"{cooperativeShortName}: your request for {Kg(requestedKg)} kg of {productName} is declined. {note}")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["cooperative_short_name"] = cooperativeShortName,
            ["requested_kg"] = KgValue(requestedKg),
            ["product_name"] = productName,
            ["accepted"] = false,
            ["note"] = note,
        },
    };

    /// <summary>MSG-SALE-04 (SALE-04.3): a Zone Manager merges two profiles after confirming that the Owner agreed.</summary>
    /// <param name="mergedDebt">The amount as shown, e.g. "1,000,000".</param>
    public static UserMessage CustomerMergeNeedsOwnerAgreement(
        string mergedName, string mergedPhone, string mergedDebt, string keptName, string keptPhone, string keptDebt) => new(
        Sale04,
        $"Has the Cooperative Owner agreed to merge {mergedName} ({mergedPhone}, debt {mergedDebt} VND) into {keptName} ({keptPhone}, debt {keptDebt} VND)? The merge cannot be undone.");

    /// <summary>MSG-SALE-05 (SALE-04.3.E1): the profile to merge has a trader account.</summary>
    /// <param name="bothLinked">The kept profile has one too, so the two are not merged at all.</param>
    public static UserMessage CustomerMergeRefused(string mergedName, string keptName, bool bothLinked) => new(
        Sale05,
        $"{mergedName} cannot be merged into {keptName}: {(bothLinked ? "both have a trader account" : $"keep {mergedName} instead, so that its trader account keeps its data")}.");

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

    public static readonly UserMessage NotThousands = new(Legacy.Msg41, "The amount must be a multiple of 1,000 VND.");

    public static UserMessage ExceedsLimit(string field, string limit) => new(Legacy.Msg45, $"{field} exceeds the allowed limit ({limit}).");

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

    /// <summary>A date as shown in messages, e.g. "29/11/2026".</summary>
    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>A weight as shown in messages, with no trailing zeros, e.g. "1500.5".</summary>
    private static string Kg(decimal kg) => kg.ToString("0.###", CultureInfo.InvariantCulture);

    // The stored parameter reads the same whether the value came from a request or from a decimal(18,3) column.
    private static decimal KgValue(decimal kg) => decimal.Parse(Kg(kg), CultureInfo.InvariantCulture);

    /// <summary>A VND amount as shown in messages, e.g. "1,250,500" (never rounded, BR-PURCHASE-02).</summary>
    private static string Vnd(decimal amount) => amount.ToString("#,0.###", CultureInfo.InvariantCulture);

    /// <summary>A quantity as shown in messages, e.g. "12.5" (at most 3 decimals).</summary>
    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // {document} of MSG-FARM-01/02: "the license number with its zone".
    private static string LicenseDocument(string? documentTypeName, string documentNumber, string zoneCode) =>
        $"{documentTypeName ?? "Zone license"} {documentNumber} ({zoneCode})";

    // {document} of MSG-FARM-01/02: "the certification name and number".
    private static string CertificationDocument(string name, string? number) => number is null ? name : $"{name} ({number})";

    // zone_codes stays a list (one zone since PR 0.8), as the clients of PR 0.5 read it.
    private static Dictionary<string, object?> LicenseParameters(
        string? documentType, string documentNumber, string zoneCode, DateOnly expiryDate, int? days) => WithDays(new Dictionary<string, object?>
    {
        ["document_type"] = documentType,
        ["document_number"] = documentNumber,
        ["zone_codes"] = new[] { zoneCode },
        ["expiry_date"] = expiryDate,
    }, days);

    private static Dictionary<string, object?> CertificationParameters(string name, string? number, DateOnly expiryDate, int? days) =>
        WithDays(new Dictionary<string, object?>
        {
            ["certification_name"] = name,
            ["certification_number"] = number,
            ["expiry_date"] = expiryDate,
        }, days);

    private static Dictionary<string, object?> WithDays(Dictionary<string, object?> parameters, int? days)
    {
        if (days is { } value)
        {
            parameters["days"] = value;
        }

        return parameters;
    }

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
