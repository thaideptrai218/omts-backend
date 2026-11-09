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

    /// <summary>MSG-GEN-04: the confirmation of an action the audit log records, asked by the client before it sends.</summary>
    /// <param name="action">What is done, in the words of the SRS, e.g. "correct traceability data".</param>
    public static UserMessage ConfirmAction(string action) => new(Gen04, $"Are you sure you want to {action}? This action is recorded in the audit log.", Legacy.Msg09);

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

    /// <summary>
    /// MSG-DEBT-03 (BR-DEBT-06): the Owner's notice of one transaction of the Accountant, "{user_name} {transaction_text}. {debt_text}"; always pushed. The SRS gives the
    /// placeholders only: the transaction names the code, the amount and the customer, the debt text the customer debt after it.
    /// </summary>
    /// <param name="transactionKind">Payment, PaidBack, PaymentCancelled or PaidBackCancelled: the atomic parameter clients word it from.</param>
    /// <param name="customerName">Null for a payment without a customer profile (BR-DEBT-03).</param>
    /// <param name="debtAmount">The customer debt after it; null without a profile.</param>
    public static UserMessage CustomerTransaction(
        string userName, string transactionKind, string paymentCode, decimal amount, string? customerName, decimal? debtAmount)
    {
        var paidBack = transactionKind.StartsWith("PaidBack", StringComparison.Ordinal);
        var verb = transactionKind.EndsWith("Cancelled", StringComparison.Ordinal) ? "cancelled" : "recorded";
        var who = customerName is null ? "with no customer profile" : $"{(paidBack ? "to" : "from")} {customerName}";
        var transaction = $"{verb} {(paidBack ? "money paid back" : "payment")} {paymentCode} of {Vnd(amount)} VND {who}";
        var debtText = customerName is not null && debtAmount is { } debt ? " " + CustomerDebtAfterText(customerName, debt) : string.Empty;
        return new UserMessage(Debt03, $"{userName} {transaction}.{debtText}")
        {
            Parameters = new Dictionary<string, object?>
            {
                ["user_name"] = userName,
                ["transaction_kind"] = transactionKind,
                ["payment_code"] = paymentCode,
                ["amount"] = amount,
                ["customer_name"] = customerName,
                ["debt_amount"] = debtAmount,
            },
        };
    }

    /// <summary>The codes MSG-DEBT-03 names for each part of one list of confirmations; the others are "and N more" (the audit rows keep every code).</summary>
    public const int BatchCodesNamed = 10;

    /// <summary>The customer debts MSG-DEBT-03 names for one list of confirmations; the others are counted.</summary>
    public const int BatchDebtsNamed = 5;

    /// <summary>
    /// MSG-DEBT-03 for one list of payments Awaiting confirmation (DEBT-02.2 step 6): one notice names the payments confirmed and cancelled. Confirmations change no debt
    /// (BR-DEBT-02); the debt text names the debt after each cancellation of a profile's payment. A list may hold up to 200 payments (CustomerPaymentRules.MaxBatchSize),
    /// so the notice names the first <see cref="BatchCodesNamed"/> codes of each part and the first <see cref="BatchDebtsNamed"/> debts and counts the rest: its text and
    /// parameters always fit the Notification row (2000 characters each), which would otherwise cut the text and drop the parameters.
    /// </summary>
    /// <param name="debts">The debt after the save of each profile a cancelled payment belonged to.</param>
    public static UserMessage CustomerPaymentsBatch(
        string userName, IReadOnlyList<(string Code, decimal Amount)> confirmed, IReadOnlyList<(string Code, decimal Amount)> cancelled,
        IReadOnlyList<(string CustomerName, decimal Debt)> debts)
    {
        static string Codes(IReadOnlyList<(string Code, decimal Amount)> payments) =>
            string.Join(", ", payments.Take(BatchCodesNamed).Select(p => p.Code)) + (payments.Count > BatchCodesNamed ? $" and {payments.Count - BatchCodesNamed} more" : "");

        static string Part(string verb, IReadOnlyList<(string Code, decimal Amount)> payments) =>
            $"{verb} {payments.Count} payment{(payments.Count == 1 ? "" : "s")} ({Codes(payments)}) of {Vnd(payments.Sum(p => p.Amount))} VND";

        static int More(int count, int named) => Math.Max(0, count - named);

        var parts = new List<string>();
        if (confirmed.Count > 0)
        {
            parts.Add(Part("confirmed", confirmed));
        }

        if (cancelled.Count > 0)
        {
            parts.Add(Part("cancelled", cancelled));
        }

        var namedDebts = debts.Take(BatchDebtsNamed).ToList();
        var moreDebts = More(debts.Count, BatchDebtsNamed);
        var debtText = cancelled.Count == 0
            ? "The customer debts have not changed."
            : string.Join(' ', namedDebts.Select(d => CustomerDebtAfterText(d.CustomerName, d.Debt)))
                + (moreDebts == 0 ? "" : moreDebts == 1 ? " The debt of 1 more customer has changed." : $" The debts of {moreDebts} more customers have changed.");
        return new UserMessage(Debt03, $"{userName} {string.Join(" and ", parts)}. {debtText}".TrimEnd())
        {
            Parameters = new Dictionary<string, object?>
            {
                ["user_name"] = userName,
                ["transaction_kind"] = "Confirmations",
                ["confirmed_codes"] = confirmed.Take(BatchCodesNamed).Select(p => p.Code).ToList(),
                ["confirmed_count"] = confirmed.Count,
                ["confirmed_more_count"] = More(confirmed.Count, BatchCodesNamed),
                ["confirmed_amount"] = confirmed.Sum(p => p.Amount),
                ["cancelled_codes"] = cancelled.Take(BatchCodesNamed).Select(p => p.Code).ToList(),
                ["cancelled_count"] = cancelled.Count,
                ["cancelled_more_count"] = More(cancelled.Count, BatchCodesNamed),
                ["cancelled_amount"] = cancelled.Sum(p => p.Amount),
                ["amount"] = confirmed.Sum(p => p.Amount) + cancelled.Sum(p => p.Amount),
                ["debts"] = namedDebts.Select(d => new Dictionary<string, object?> { ["customer_name"] = d.CustomerName, ["debt_amount"] = d.Debt }).ToList(),
                ["debts_more_count"] = moreDebts,
            },
        };
    }

    /// <summary>MSG-DEBT-04 (DEBT-02.1): a payment larger than the customer debt, or received when the debt is zero or below; a confirmation.</summary>
    /// <param name="debtAmount">The debt before the payment; below 0 it is already money owed back.</param>
    /// <param name="owedBackAmount">What the cooperative will owe the customer after it (positive).</param>
    public static UserMessage CustomerPaymentAboveDebt(string customerName, decimal debtAmount, decimal amount, decimal owedBackAmount)
    {
        var debtText = debtAmount > 0
            ? $"{customerName} owes the cooperative {Vnd(debtAmount)} VND."
            : debtAmount == 0
                ? $"{customerName} owes the cooperative nothing."
                : $"The cooperative already owes {customerName} {Vnd(-debtAmount)} VND.";
        return new UserMessage(
            Debt04,
            $"{debtText} After this payment of {Vnd(amount)} VND, the cooperative will owe {customerName} {Vnd(owedBackAmount)} VND. Do you want to save the payment?");
    }

    /// <summary>MSG-DEBT-05 (DEBT-02.5): a customer payment that is not cancelled already has the bank reference; a confirmation.</summary>
    /// <param name="customerName">Null for a payment without a customer profile: then no customer is named.</param>
    public static UserMessage CustomerBankReferenceAlreadyRecorded(string paymentCode, decimal amount, DateOnly paymentDate, string? customerName, string bankReference) => new(
        Debt05,
        $"Payment {paymentCode} ({Vnd(amount)} VND, {Date(paymentDate)}{(customerName is null ? "" : ", " + customerName)}) already has bank reference {bankReference}. Do you want to save this one as well?");

    /// <summary>MSG-DEBT-06 (DEBT-02.3.E1): the money paid back is more than the money owed back to the customer.</summary>
    /// <param name="owedBackAmount">The money owed back now, 0 when the customer owes the cooperative.</param>
    public static UserMessage PaidBackAboveOwedBack(string customerName, decimal owedBackAmount) => new(
        Debt06, $"The cooperative owes {customerName} {Vnd(owedBackAmount)} VND. The money paid back cannot be more than this amount.");

    /// <summary>The {debt_text} of MSG-DEBT-03: the customer debt after the transaction, or the money owed back when it is below zero.</summary>
    private static string CustomerDebtAfterText(string customerName, decimal debt) => debt >= 0
        ? $"{customerName} now owes the cooperative {Vnd(debt)} VND."
        : $"The cooperative now owes {customerName} {Vnd(-debt)} VND.";

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

    /// <summary>MSG-CYCLE-01 (CYCLE-02.1): the confirmation before marking the seeding complete; the second sentence appears only when some lanes have no seed yet.</summary>
    public static UserMessage FinishSeedingQuestion(string raftCode, IReadOnlyList<int> notSeededLanes)
    {
        var lanes = string.Join(", ", notSeededLanes);
        var noSeed = notSeededLanes.Count == 0 ? string.Empty : $" {notSeededLanes.Count} lane(s) have no seed yet (lanes {lanes}); you can still seed them later.";
        return new UserMessage(Cycle01, $"Finish seeding of raft {raftCode}?{noSeed} The Cooperative Owner will be asked to review the seeding.")
        {
            Parameters = new Dictionary<string, object?> { ["raft_code"] = raftCode, ["count"] = notSeededLanes.Count, ["lane_numbers"] = notSeededLanes },
        };
    }

    /// <summary>MSG-CYCLE-02 (CYCLE-02.1): notification to the Cooperative Owner when the seeding of a raft is marked complete.</summary>
    public static UserMessage SeedingFinished(string raftCode, int seededCount, int laneCount) => new(
        Cycle02, $"Raft {raftCode}: seeding is finished; {seededCount} of {laneCount} lanes have seed. Please review the seeding sessions.")
    {
        Parameters = new Dictionary<string, object?> { ["raft_code"] = raftCode, ["seeded_count"] = seededCount, ["lane_count"] = laneCount },
    };

    /// <summary>MSG-CYCLE-03 (CYCLE-02.0.E1): no seed lot can be chosen (GBR-04).</summary>
    public static readonly UserMessage NoSeedLot = new(
        Cycle03, "There is no seed lot to choose: no completed purchase receipt for seed exists yet. Please complete the purchase receipt for the seed first.");

    /// <summary>MSG-CYCLE-04 (CYCLE-04): notification to the Cooperative Owner when a cultivation log is saved.</summary>
    public static UserMessage CultivationLogRecorded(string raftCode, DateOnly visitDate) => new(
        Cycle04, $"Raft {raftCode}: a cultivation log was recorded for the visit on {Date(visitDate)}. Please review it.")
    {
        Parameters = new Dictionary<string, object?> { ["raft_code"] = raftCode, ["visit_date"] = visitDate },
    };

    /// <summary>MSG-CYCLE-05 (CYCLE-06 step 4): the confirmation before closing a cycle.</summary>
    public static UserMessage CloseCycleQuestion(string raftCode) => new(
        Cycle05, $"Close the cultivation cycle of raft {raftCode}? After closing, the cycle cannot be reopened and its records can no longer be changed.")
    {
        Parameters = new Dictionary<string, object?> { ["raft_code"] = raftCode },
    };

    /// <summary>
    /// MSG-CYCLE-06 (CYCLE-06 step 6): the summary of a closed cycle to the Cooperative Owner, with the figures of the close day (BR-CYCLE-06). {early_note} is empty after a normal
    /// close, with the space before it, and "early; lanes not harvested: {lane_numbers}" after an early close; "early" alone when no lane had seed. A seed quantity with no unit
    /// (no seeding session) drops the space before the unit.
    /// </summary>
    public static UserMessage CycleClosedSummary(
        string raftCode, DateOnly closeDate, bool closedEarly, IReadOnlyList<int> notHarvestedLanes, decimal seedQuantity, string seedUnit, decimal kgHarvested, int days)
    {
        var earlyNote = !closedEarly ? string.Empty : notHarvestedLanes.Count == 0 ? " early" : $" early; lanes not harvested: {string.Join(", ", notHarvestedLanes)}";
        var unit = string.IsNullOrWhiteSpace(seedUnit) ? string.Empty : $" {seedUnit}";
        return new UserMessage(
            Cycle06,
            $"Raft {raftCode}: the cultivation cycle was closed on {Date(closeDate)}{earlyNote}. Seed hung: {Quantity(seedQuantity)}{unit}. Harvested: about {Quantity(kgHarvested)} kg. Days of farming: {days.ToString(CultureInfo.InvariantCulture)}. Please review the summary.")
        {
            Parameters = new Dictionary<string, object?>
            {
                ["raft_code"] = raftCode,
                ["close_date"] = closeDate,
                ["closed_early"] = closedEarly,
                ["lane_numbers"] = notHarvestedLanes,
                ["seed_quantity"] = decimal.Parse(Quantity(seedQuantity), CultureInfo.InvariantCulture),
                ["seed_unit"] = string.IsNullOrWhiteSpace(seedUnit) ? null : seedUnit,
                ["kg_harvested"] = decimal.Parse(Quantity(kgHarvested), CultureInfo.InvariantCulture),
                ["days"] = days,
            },
        };
    }

    /// <summary>
    /// MSG-CYCLE-07 (CYCLE-06.1): an early close needs the Cooperative Owner's approval (BR-CYCLE-07). {reason} is "no seeding session is recorded" when the cycle has none, else
    /// "lanes {lane_numbers} are not harvested".
    /// </summary>
    public static UserMessage EarlyCloseQuestion(IReadOnlyList<int> notHarvestedLanes)
    {
        var reason = notHarvestedLanes.Count == 0 ? "no seeding session is recorded" : $"lanes {string.Join(", ", notHarvestedLanes)} are not harvested";
        return new UserMessage(
            Cycle07,
            $"This cycle is not finished: {reason}. Oysters left on lanes that are Growing cannot be harvested in OMTS after the close. Closing it now needs the Cooperative Owner's approval. Has the Cooperative Owner approved closing it now?")
        {
            Parameters = new Dictionary<string, object?> { ["no_seeding"] = notHarvestedLanes.Count == 0, ["lane_numbers"] = notHarvestedLanes },
        };
    }

    /// <summary>MSG-CYCLE-08 (BR-CYCLE-07): reminder when every seeded lane of an Open cycle is Harvested.</summary>
    public static UserMessage CycleCloseReminder(string raftCode) => new(Cycle08, $"Raft {raftCode}: every seeded lane is harvested. Please close its cultivation cycle.")
    {
        Parameters = new Dictionary<string, object?> { ["raft_code"] = raftCode },
    };

    /// <summary>MSG-HARVEST-01 (BF-03 step 7): notification to the Cooperative Owner when a harvest lot is recorded.</summary>
    public static UserMessage HarvestLotRecorded(string raftCode, DateOnly harvestDate, string lotCode, int cageCount, decimal estimatedKg) => new(
        Harvest01, $"Raft {raftCode}: the harvest of {Date(harvestDate)} is recorded as lot {lotCode} ({cageCount.ToString(CultureInfo.InvariantCulture)} cages, about {Quantity(estimatedKg)} kg).")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["raft_code"] = raftCode,
            ["harvest_date"] = harvestDate,
            ["lot_code"] = lotCode,
            ["cage_count"] = cageCount,
            ["estimated_kg"] = decimal.Parse(Quantity(estimatedKg), CultureInfo.InvariantCulture),
        },
    };

    /// <summary>MSG-HARVEST-03 (HARVEST-03 step 4): the days before automatic close are lowered.</summary>
    public static UserMessage AutoCloseDaysLowered(int days, int count) => new(
        Harvest03,
        $"With {days.ToString(CultureInfo.InvariantCulture)} days, the next daily close check will close {count.ToString(CultureInfo.InvariantCulture)} lots that are still for sale. Do you want to save?")
    {
        Parameters = new Dictionary<string, object?> { ["days"] = days, ["count"] = count },
    };

    /// <summary>MSG-HARVEST-04 (HARVEST-04 step 3): the yield report holds lots still for sale (Open or Reopened), whose figures can still change (BR-HARVEST-07).</summary>
    public static readonly UserMessage YieldNotFinal = new(Harvest04, "Some lots are still for sale, so their figures may still change.");

    /// <summary>MSG-HARVEST-05 (GFR-05): the next daily close check closes this lot still for sale.</summary>
    /// <param name="closeDate">The day of that check: the lot is closed at its start.</param>
    public static UserMessage HarvestLotAutoCloseWarning(string lotCode, string raftCode, DateOnly closeDate) => new(
        Harvest05, $"Lot {lotCode} (raft {raftCode}) will be closed automatically at the start of {Date(closeDate)}. Please sell it before then or close it yourself.")
    {
        Parameters = new Dictionary<string, object?> { ["lot_code"] = lotCode, ["raft_code"] = raftCode, ["close_date"] = closeDate },
    };

    // ---- MSG-TRACE ----

    /// <summary>
    /// MSG-TRACE-01 (TRACE-01.1, TRACE-01.0.E1): the lot misses origin information that the provenance page needs (BR-TRACE-01) and stays Not published. On the Trace lot screen
    /// (TRACE-01.2) there is no invoice, so the sentence about the invoice is left out.
    /// </summary>
    /// <param name="missingItems">What the lot misses, each named (lanes without a seed lot).</param>
    /// <param name="atEndOfSale">The check ran at the end of a sale (SALE-05.9).</param>
    public static UserMessage ProvenanceNotReady(string lotCode, IReadOnlyList<OriginGap> missingItems, bool atEndOfSale)
    {
        var invoice = atEndOfSale ? " The invoice is saved; the other lots are not affected." : string.Empty;
        return new UserMessage(Trace01, $"Harvest lot {lotCode} cannot be published yet: {string.Join("; ", missingItems.Select(m => m.Describe()))}.{invoice}")
        {
            Parameters = new Dictionary<string, object?> { ["lot_code"] = lotCode, ["missing_items"] = missingItems },
        };
    }

    /// <summary>
    /// MSG-TRACE-02 (TRACE-02.1): the backward trace of the lot finds missing data. The system names it and never chooses a seed lot or a raft by itself (BR-TRACE-03); the items are
    /// those of MSG-TRACE-01 (BR-TRACE-01).
    /// </summary>
    public static UserMessage TraceIncomplete(string lotCode, IReadOnlyList<OriginGap> missingItems) => new(
        Trace02,
        $"Missing data in the trace of harvest lot {lotCode}: {string.Join("; ", missingItems.Select(m => m.Describe()))}. Please ask the Cooperative Owner whether the data must be corrected.")
    {
        Parameters = new Dictionary<string, object?> { ["lot_code"] = lotCode, ["missing_items"] = missingItems },
    };

    /// <summary>MSG-TRACE-03 (TRACE-03.0.E3): the correction would leave a lane of a harvest lot with no seed lot (BR-TRACE-01). The text names the first such lane.</summary>
    public static UserMessage LaneWithoutSeedLot(int laneNumber, string lotCode) => new(
        Trace03,
        $"The correction is not saved: lane {laneNumber.ToString(CultureInfo.InvariantCulture)} of harvest lot {lotCode} would have no seed lot. Please keep at least one seed lot for this lane.")
    {
        Parameters = new Dictionary<string, object?> { ["lane_number"] = laneNumber, ["lot_code"] = lotCode },
    };

    /// <summary>
    /// MSG-TRACE-04 (TRACE-03 step 6): a Zone Manager confirms that the Cooperative Owner agreed in person that the data is wrong (BR-TRACE-04). With no harvest lot whose origin
    /// information changes (a cultivation log, BR-TRACE-05), the first sentence is left out.
    /// </summary>
    public static UserMessage TraceCorrectionAgreement(IReadOnlyList<string> lotCodes)
    {
        var changes = lotCodes.Count == 0 ? string.Empty : $"This correction changes the origin information of harvest lots {string.Join(", ", lotCodes)}. ";
        return new UserMessage(Trace04, $"{changes}Has the Cooperative Owner agreed in person that the data is wrong?")
        {
            Parameters = new Dictionary<string, object?> { ["lot_codes"] = lotCodes },
        };
    }

    /// <summary>
    /// MSG-TRACE-05 (TRACE-04 step 6, BR-TRACE-07): the withdrawal notice to one trader, listing their invoices; never the reason. Without a cooperative phone number yet (FARM-01)
    /// the trader is asked to call the cooperative.
    /// </summary>
    public static UserMessage ProvenanceWithdrawnNotice(string cooperativeShortName, string lotCode, IReadOnlyList<string> invoiceCodes, string? cooperativePhone) => new(
        Trace05,
        $"{cooperativeShortName}: the origin information of oyster lot {lotCode} (invoice {string.Join(", ", invoiceCodes)}) has been withdrawn. You do not need to return the oysters. Please call {cooperativePhone ?? "the cooperative"}.")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["cooperative_short_name"] = cooperativeShortName, ["lot_code"] = lotCode, ["invoice_codes"] = invoiceCodes, ["cooperative_phone"] = cooperativePhone,
        },
    };

    /// <summary>MSG-TRACE-06 (TRACE-04.2): no current sales invoice sells the lot, so the withdrawal notifies nobody.</summary>
    public static UserMessage NoTraderToNotify(string lotCode) => new(Trace06, $"No current sales invoice sells harvest lot {lotCode}; no trader will be notified.")
    {
        Parameters = new Dictionary<string, object?> { ["lot_code"] = lotCode },
    };

    // ---- MSG-ORIGIN ----

    /// <summary>
    /// MSG-ORIGIN-01 (ORIGIN-01.1): a lot of the provenance page is Withdrawn. The page shows the withdrawal date and the cooperative phone, never the reason (BR-TRACE-06,
    /// BR-ORIGIN-05).
    /// </summary>
    /// <param name="cooperativePhone">Null shows "–": the profile was saved before the phone number became mandatory.</param>
    public static UserMessage ProvenanceLotWithdrawn(string cooperativeShortName, string lotCode, DateOnly withdrawalDate, string? cooperativePhone) => new(
        Origin01,
        $"{cooperativeShortName} withdrew the origin information of lot {lotCode} on {Date(withdrawalDate)}. For details, please call {cooperativePhone ?? NoValue}.")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["cooperative_short_name"] = cooperativeShortName, ["lot_code"] = lotCode, ["withdrawal_date"] = withdrawalDate, ["cooperative_phone"] = cooperativePhone,
        },
    };

    /// <summary>MSG-ORIGIN-02 (ORIGIN-01.2): no lot of the invoice is Published or Withdrawn (BR-ORIGIN-05); the page does not say why.</summary>
    public static UserMessage ProvenanceNoLots(string? cooperativePhone) => new(
        Origin02, $"No origin information is shown for this invoice. For details, please call {cooperativePhone ?? NoValue}.")
    {
        Parameters = new Dictionary<string, object?> { ["cooperative_phone"] = cooperativePhone },
    };

    /// <summary>MSG-ORIGIN-03 (ORIGIN-01.0.E1): no sales invoice has the QR code of the link; also for a link that is no QR code at all, so nothing tells them apart.</summary>
    public static readonly UserMessage ProvenanceUnknownQrCode = new(
        Origin03, "No invoice was found for this QR code. Please scan the QR code on the invoice again, or open the link in the invoice message.");

    /// <summary>MSG-ORIGIN-04 (ORIGIN-01.0.E2): the invoice of the QR code is Cancelled; the page shows the cooperative block only (BR-ORIGIN-05).</summary>
    public static UserMessage ProvenanceInvoiceCancelled(string? cooperativePhone) => new(
        Origin04, $"This invoice has been cancelled, so no origin information is shown. For details, please call {cooperativePhone ?? NoValue}.")
    {
        Parameters = new Dictionary<string, object?> { ["cooperative_phone"] = cooperativePhone },
    };

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

    /// <summary>MSG-PURCHASE-02 (PURCHASE-02 step 4): a Zone Manager confirms that the Owner approved the return in person; a confirmation.</summary>
    public static UserMessage ReturnOwnerApproval(decimal returnAmount, string supplierName) => new(
        Purchase02, $"Has the Cooperative Owner approved this return of {Vnd(returnAmount)} VND to {supplierName} in person?");

    /// <summary>MSG-PURCHASE-03 (PURCHASE-02.0.E1): a quantity to return is not greater than 0 or above what is left.</summary>
    public static UserMessage ReturnQuantityOutsideLimit(string itemName, string maxQuantity, string unit, string limitExplanation) => new(
        Purchase03, $"{itemName}: enter a quantity greater than 0 and at most {maxQuantity} {unit}. {limitExplanation}".TrimEnd(), Legacy.Msg59);

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

    /// <summary>MSG-LABOR-04 (BR-LABOR-13): a change is dated in a pay period whose payroll, or a later one, is Awaiting finalization or Finalized.</summary>
    /// <param name="period">The name of the payroll's period, e.g. "10/2026".</param>
    /// <param name="payrollStatus">The display name of its status.</param>
    /// <param name="lockedUntil">The change can be dated after it: the last day of the latest locked payroll, or the employee's first type date when that is later.</param>
    /// <param name="canBeSentBack">Every locked payroll in the way is Awaiting finalization, so the Owner can send it back (LABOR-13.1).</param>
    public static UserMessage PayrollLocksDate(string period, string payrollStatus, DateOnly date, DateOnly lockedUntil, bool canBeSentBack)
    {
        var nextStep = canBeSentBack
            ? $"Choose a date after {Date(lockedUntil)}, or ask the Cooperative Owner to send the payroll back first."
            : $"Choose a date after {Date(lockedUntil)}.";
        return new UserMessage(Labor04, $"The payroll of {period} is {payrollStatus}, so this change cannot apply to {Date(date)}. {nextStep}")
        {
            Parameters = new Dictionary<string, object?> { ["period"] = period, ["payroll_status"] = payrollStatus, ["date"] = date, ["next_step"] = nextStep },
        };
    }

    /// <summary>MSG-LABOR-36 (LABOR-16 step 3): asked before saving the payroll settings.</summary>
    /// <param name="period1">The next pay period as shown to the user.</param>
    /// <param name="automatic">Automatic payroll is on: the note about it is added.</param>
    public static UserMessage NextPayPeriods(string period1, string period2, bool automatic)
    {
        var autoNote = automatic ? ", and OMTS creates the payroll of each new pay period at its start" : string.Empty;
        return new UserMessage(Labor36, $"With these settings, the next pay periods are {period1} and {period2}{autoNote}. Do you want to save?")
        {
            Parameters = new Dictionary<string, object?> { ["period_1"] = period1, ["period_2"] = period2, ["auto_note"] = autoNote },
        };
    }

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

    // ---- MSG-SALE: the sales invoice (SALE-05) ----

    /// <summary>
    /// MSG-SALE-06 (SALE-05.6 to SALE-05.8): the invoice is completed. {payment_text} lists the payments joined by "; ": the cash recorded (the part subtracted from the debt included)
    /// and the part added to the debt. An invoice of 0 VND reads "0 VND collected in cash".
    /// </summary>
    public static UserMessage SaleCompleted(string invoiceCode, decimal cashAmount, decimal laterAmount, string customerName)
    {
        var parts = new List<string>();
        if (cashAmount > 0 || laterAmount == 0)
        {
            parts.Add($"{Vnd(cashAmount)} VND collected in cash");
        }

        if (laterAmount > 0)
        {
            parts.Add($"{Vnd(laterAmount)} VND added to the debt of {customerName}");
        }

        var paymentText = string.Join("; ", parts);
        return new UserMessage(Sale06, $"Invoice {invoiceCode} is completed: {paymentText}.")
        {
            Parameters = new Dictionary<string, object?>
            {
                ["invoice_code"] = invoiceCode,
                ["payment_text"] = paymentText,
                ["cash_amount"] = cashAmount,
                ["later_amount"] = laterAmount,
                ["customer_name"] = customerName,
            },
        };
    }

    /// <summary>MSG-SALE-08 (BR-SALE-03): a new sale, to the Cooperative Owner. {customer_name} is "a walk-in customer" without a profile; {price_text} is empty or starts with a space.</summary>
    /// <param name="paymentTerm">"Pay now" or "Pay later".</param>
    public static UserMessage SaleNotice(string userName, decimal totalKg, string customerName, decimal totalAmount, string invoiceCode, string paymentTerm, string priceText) => new(
        Sale08, $"{userName} sold {Kg(totalKg)} kg to {customerName} for {Vnd(totalAmount)} VND (invoice {invoiceCode}, {paymentTerm}).{priceText}")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["user_name"] = userName,
            ["total_kg"] = KgValue(totalKg),
            ["customer_name"] = customerName,
            ["total_amount"] = totalAmount,
            ["invoice_code"] = invoiceCode,
            ["payment_term"] = paymentTerm,
            ["price_text"] = priceText,
        },
    };

    /// <summary>
    /// {price_text} of MSG-SALE-08: empty, or the prices the Cooperative Owner decided and the discount, e.g. " Price decided by the Cooperative Owner: 55,000 VND/kg (list price 60,000);
    /// discount 200,000 VND.". Not a message of its own.
    /// </summary>
    public static string SalePriceText(IReadOnlyList<(decimal UnitPrice, decimal ListPrice)> decidedPrices, decimal discount)
    {
        var parts = new List<string>();
        if (decidedPrices.Count > 0)
        {
            parts.Add("Price decided by the Cooperative Owner: " + string.Join(", ", decidedPrices.Distinct().Select(p => $"{Vnd(p.UnitPrice)} VND/kg (list price {Vnd(p.ListPrice)})")));
        }

        if (discount > 0)
        {
            parts.Add($"discount {Vnd(discount)} VND");
        }

        return parts.Count == 0 ? string.Empty : $" {Capitalized(string.Join("; ", parts))}.";
    }

    /// <summary>MSG-SALE-10 (SALE-05.5): a unit price other than the list price, entered by a Zone Manager.</summary>
    public static UserMessage PriceNeedsOwnerDecision(string productName, decimal listPrice, decimal unitPrice) => new(
        Sale10, $"{productName}: the list price is {Vnd(listPrice)} VND/kg, and you entered {Vnd(unitPrice)} VND/kg. Has the Cooperative Owner decided this price?")
    {
        Parameters = new Dictionary<string, object?> { ["product_name"] = productName, ["list_price"] = listPrice, ["unit_price"] = unitPrice },
    };

    /// <summary>MSG-SALE-10 (SALE-05.5), the content the SRS gives for a discount.</summary>
    public static UserMessage DiscountNeedsOwnerDecision(decimal discountAmount) => new(
        Sale10, $"Has the Cooperative Owner decided a discount of {Vnd(discountAmount)} VND on this invoice?")
    {
        Parameters = new Dictionary<string, object?> { ["discount_amount"] = discountAmount },
    };

    /// <summary>MSG-SALE-11 (SALE-05.6 step 3): cash above the total, for a customer who has a debt. {subtract_amount} is the extra but at most the debt; {change_amount} the rest.</summary>
    public static UserMessage CashAboveTotal(decimal excessAmount, string customerName, decimal debtAmount, decimal subtractAmount, decimal changeAmount) => new(
        Sale11,
        $"The trader paid {Vnd(excessAmount)} VND more than the total. {customerName} owes {Vnd(debtAmount)} VND. Subtract {Vnd(subtractAmount)} VND from this debt and give back {Vnd(changeAmount)} VND as change?")
    {
        Parameters = new Dictionary<string, object?>
        {
            ["excess_amount"] = excessAmount,
            ["customer_name"] = customerName,
            ["debt_amount"] = debtAmount,
            ["subtract_amount"] = subtractAmount,
            ["change_amount"] = changeAmount,
        },
    };

    /// <summary>MSG-SALE-12 (SALE-05.8 step 2): Pay later without a linked request that agrees to it. {later_amount} is the total, or the rest after the cash part.</summary>
    public static UserMessage PayLaterNeedsOwnerAgreement(string customerName, decimal laterAmount, decimal debtAmount) => new(
        Sale12, $"Has the Cooperative Owner agreed that {customerName} pays {Vnd(laterAmount)} VND of this invoice later? {customerName} owes {Vnd(debtAmount)} VND now.")
    {
        Parameters = new Dictionary<string, object?> { ["customer_name"] = customerName, ["later_amount"] = laterAmount, ["debt_amount"] = debtAmount },
    };

    // ---- MSG-SALE: sending the sales invoice (SALE-05 end, SALE-06) ----

    /// <summary>MSG-SALE-07 (SALE-05 step 9): the question at the end of the sale, answered by <c>wantsQrCode</c>.</summary>
    public static UserMessage WantsQrCode(string invoiceCode) => new(
        Sale07, $"Does the trader want the QR code of invoice {invoiceCode}, to see where the oysters come from?")
    {
        Parameters = new Dictionary<string, object?> { ["invoice_code"] = invoiceCode },
    };

    /// <summary>
    /// MSG-SALE-15 (BR-SALE-09): the invoice message to the trader, at the latest values of the invoice. {to_pay_text} is empty when nothing is left to pay; it names the receiving
    /// bank account when one is set up, else only the amount (no VietQR can be paid without it). {origin_text} is empty without a QR code. The parameters are atomic values;
    /// <c>bank_account</c> and <c>bank_name</c> are present only when the text names the account, which is how DEBT-01 counts the codes already sent (BR-DEBT-01).
    /// </summary>
    /// <param name="corrected">Sent again after a correction (SALE-07): " (corrected)".</param>
    /// <param name="paidAmount">The part of the total that the payments of the invoice which are not Cancelled cover.</param>
    /// <param name="toPayAmount">Total − paid, 0 or more.</param>
    /// <param name="bankAccountNumber">The receiving bank account (DEBT-01), null when none is set up.</param>
    /// <param name="provenanceLink">The link of the QR code of the invoice, null when it has none.</param>
    public static UserMessage InvoiceMessage(
        string cooperativeShortName, string invoiceCode, bool corrected, DateOnly invoiceDate, decimal totalKg, decimal totalAmount, decimal paidAmount, decimal toPayAmount,
        string? bankAccountNumber, string? bankName, string? provenanceLink)
    {
        var namesAccount = toPayAmount > 0m && bankAccountNumber is not null;
        var toPayText = toPayAmount <= 0m
            ? string.Empty
            : namesAccount
                ? $"; to pay {Vnd(toPayAmount)} VND to account {bankAccountNumber} ({bankName}), transfer content {invoiceCode}"
                : $"; to pay {Vnd(toPayAmount)} VND";
        var originText = provenanceLink is null ? string.Empty : $" Origin: {provenanceLink}";
        var correctedText = corrected ? " (corrected)" : string.Empty;
        var parameters = new Dictionary<string, object?>
        {
            ["cooperative_short_name"] = cooperativeShortName,
            ["invoice_code"] = invoiceCode,
            ["corrected"] = corrected,
            ["invoice_date"] = invoiceDate,
            ["total_kg"] = KgValue(totalKg),
            ["total_amount"] = totalAmount,
            ["paid_amount"] = paidAmount,
            ["to_pay_amount"] = toPayAmount,
            ["provenance_link"] = provenanceLink,
        };
        if (namesAccount)
        {
            parameters[InvoiceMessageBankAccountParameter] = bankAccountNumber;
            parameters[InvoiceMessageBankNameParameter] = bankName;
        }

        return new UserMessage(
            Sale15,
            $"{cooperativeShortName}: invoice {invoiceCode}{correctedText} of {Date(invoiceDate)}: {Kg(totalKg)} kg, {Vnd(totalAmount)} VND, paid {Vnd(paidAmount)} VND{toPayText}.{originText}")
        {
            Parameters = parameters,
        };
    }

    /// <summary>The parameter of MSG-SALE-15 that names the receiving account, read back by the DEBT-01 count of the codes already sent.</summary>
    public const string InvoiceMessageBankAccountParameter = "bank_account";

    public const string InvoiceMessageBankNameParameter = "bank_name";

    /// <summary>MSG-SALE-16 (SALE-06.0.E1): nothing is sent to a walk-in customer or to a profile without a phone number.</summary>
    public static UserMessage InvoiceNotSent(string invoiceCode, bool hasCustomerProfile)
    {
        var cause = hasCustomerProfile ? "the customer profile has no phone number" : "it has no customer profile";
        return new UserMessage(Sale16, $"Invoice {invoiceCode} is not sent: {cause}. Please show the invoice to the trader on your screen.")
        {
            Parameters = new Dictionary<string, object?> { ["invoice_code"] = invoiceCode, ["cause"] = hasCustomerProfile ? "NoPhoneNumber" : "NoCustomerProfile" },
        };
    }

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

    /// <summary>Not in the SRS list: a notice of the Zalo Mini App flows, whose text is built by the module.</summary>
    public static UserMessage MiniAppNotification(string text) => new(Legacy.MiniAppNotification, text);

    public static UserMessage ServiceNotResponding(string service) => new(
        Legacy.Msg04, $"The {service} service is not responding. The action has not been completed and the data in OMTS is unchanged; please try again later.");

    public static readonly UserMessage OldDocumentsHidden = new(
        Legacy.Msg57, "Old documents are temporarily hidden until identity verification is complete.");

    /// <summary>Not in the SRS list: the Zalo access token of the Mini App could not be checked with Zalo.</summary>
    public static readonly UserMessage ZaloTokenInvalid = new(
        Legacy.ZaloTokenInvalid, "The Zalo account could not be verified. Please open OMTS from Zalo and try again.");

    /// <summary>Not in the SRS list: the phone token of the Mini App was refused (used, expired or forged).</summary>
    public static readonly UserMessage ZaloPhoneTokenInvalid = new(
        Legacy.ZaloPhoneTokenInvalid, "The phone number could not be read from Zalo. Please try again.");

    /// <summary>Not in the SRS list: no account uses the Zalo phone number; the person may ask the cooperative for one.</summary>
    public static readonly UserMessage ZaloPhoneNotFound = new(
        Legacy.ZaloPhoneNotFound, "No OMTS account uses this phone number. Traders can send a request to the cooperative.");

    /// <summary>Not in the SRS list: the trader's request waits for the Cooperative Owner.</summary>
    public static readonly UserMessage ZaloApprovalPending = new(
        Legacy.ZaloApprovalPending, "Your request has been sent. Please wait until the cooperative approves it.");

    /// <summary>Not in the SRS list: the Cooperative Owner refused the trader's request (the reason is a separate field).</summary>
    public static readonly UserMessage ZaloRequestRejected = new(
        Legacy.ZaloRequestRejected, "The cooperative did not approve your request. You can contact the cooperative or send a new request.");

    /// <summary>Not in the SRS list: the phone number already has an account, which is linked instead of requested.</summary>
    public static readonly UserMessage ZaloAccountExists = new(
        Legacy.ZaloAccountExists, "This phone number already has an OMTS account. Please link it instead.");

    /// <summary>Not in the SRS list: the Zalo phone number is not a Vietnamese number, so no account can use it.</summary>
    public static readonly UserMessage ZaloPhoneUnsupported = new(
        Legacy.ZaloPhoneUnsupported, "OMTS accounts use Vietnamese phone numbers only. Please use a Zalo account with a Vietnamese phone number.");

    /// <summary>Not in the SRS list: the Zalo user is already linked to another OMTS account.</summary>
    public static readonly UserMessage ZaloAlreadyLinked = new(
        Legacy.ZaloAlreadyLinked, "This Zalo account is already linked to another OMTS account.");

    /// <summary>Not in the SRS list: the Zalo user has not linked an account yet (the Mini App stays a guest).</summary>
    public static readonly UserMessage ZaloNotLinked = new(Legacy.ZaloNotLinked, "This Zalo account is not linked to OMTS yet.");

    /// <summary>What a text shows for a value that is not saved.</summary>
    private const string NoValue = "–";

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
