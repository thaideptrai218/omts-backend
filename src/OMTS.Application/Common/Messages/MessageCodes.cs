namespace OMTS.Application.Common.Messages;

/// <summary>
/// The message codes the API sends. The top-level constants are codes of the SRS 27/10/2026 catalogue (Part V §2);
/// <see cref="Legacy"/> holds the codes of the old list (MSGnn, 01/10/2026) and the technical codes.
/// </summary>
public static class MessageCodes
{
    public const string Gen01 = "MSG-GEN-01";
    public const string Gen02 = "MSG-GEN-02";
    public const string Gen03 = "MSG-GEN-03";
    public const string Gen04 = "MSG-GEN-04";
    public const string Gen05 = "MSG-GEN-05";
    public const string Gen06 = "MSG-GEN-06";
    public const string Gen08 = "MSG-GEN-08";
    public const string Gen09 = "MSG-GEN-09";
    public const string Gen10 = "MSG-GEN-10";

    public const string Water01 = "MSG-WATER-01";
    public const string Water05 = "MSG-WATER-05";

    public const string Account02 = "MSG-ACCOUNT-02";
    public const string Account03 = "MSG-ACCOUNT-03";
    public const string Account04 = "MSG-ACCOUNT-04";
    public const string Account05 = "MSG-ACCOUNT-05";
    public const string Account06 = "MSG-ACCOUNT-06";
    public const string Account07 = "MSG-ACCOUNT-07";
    public const string Account08 = "MSG-ACCOUNT-08";
    public const string Account09 = "MSG-ACCOUNT-09";
    public const string Account10 = "MSG-ACCOUNT-10";
    public const string Account11 = "MSG-ACCOUNT-11";
    public const string Account13 = "MSG-ACCOUNT-13";
    public const string Account14 = "MSG-ACCOUNT-14";
    public const string Account15 = "MSG-ACCOUNT-15";
    public const string Account16 = "MSG-ACCOUNT-16";
    public const string Account17 = "MSG-ACCOUNT-17";
    public const string Account18 = "MSG-ACCOUNT-18";
    public const string Account19 = "MSG-ACCOUNT-19";
    public const string Account20 = "MSG-ACCOUNT-20";
    public const string Account21 = "MSG-ACCOUNT-21";

    public const string Purchase03 = "MSG-PURCHASE-03";
    public const string Purchase09 = "MSG-PURCHASE-09";

    public const string Labor03 = "MSG-LABOR-03";

    /// <summary>
    /// SRS 27/10 codes for situations that had no message before, so they name no legacy code (e.g. MSG-ACCOUNT-01
    /// when the trader account is linked to the existing customer profile). Empty for now.
    /// </summary>
    public static readonly IReadOnlySet<string> NewWithoutLegacyCode = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Codes of the old list. Most are only sent as <c>legacyCode</c> next to the new code, for one release.
    /// <see cref="KeptAsCode"/> lists those still sent as <c>code</c>.
    /// </summary>
    public static class Legacy
    {
        public const string Msg01 = "MSG01";
        public const string Msg02 = "MSG02";
        public const string Msg03 = "MSG03";
        public const string Msg04 = "MSG04";
        public const string Msg05 = "MSG05";
        public const string Msg06 = "MSG06";
        public const string Msg08 = "MSG08";
        public const string Msg09 = "MSG09";
        public const string Msg10 = "MSG10";
        public const string Msg11 = "MSG11";
        public const string Msg12 = "MSG12";
        public const string Msg13 = "MSG13";
        public const string Msg14 = "MSG14";
        public const string Msg15 = "MSG15";
        public const string Msg16 = "MSG16";
        public const string Msg17 = "MSG17";
        public const string Msg18 = "MSG18";
        public const string Msg19 = "MSG19";
        public const string Msg20 = "MSG20";
        public const string Msg21 = "MSG21";
        public const string Msg22 = "MSG22";
        public const string Msg23 = "MSG23";
        public const string Msg24 = "MSG24";
        public const string Msg25 = "MSG25";
        public const string Msg26 = "MSG26";
        public const string Msg27 = "MSG27";
        public const string Msg28 = "MSG28";
        public const string Msg41 = "MSG41";
        public const string Msg45 = "MSG45";
        public const string Msg57 = "MSG57";
        public const string Msg58 = "MSG58";
        public const string Msg59 = "MSG59";
        public const string Msg61 = "MSG61";
        public const string Msg62 = "MSG62";
        public const string Msg67 = "MSG67";
        public const string Msg68 = "MSG68";
        public const string Msg69 = "MSG69";

        public const string NotFound = "NOT_FOUND";
        public const string TooManyRequests = "TOO_MANY_REQUESTS";
        public const string NoVerifiedEmail = "NO_VERIFIED_EMAIL";
        public const string UnexpectedError = "UNEXPECTED_ERROR";
        public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";
        // Existing Zalo technical outcomes from #20; no SRS replacement, keep their wire codes.
        public const string ZaloTokenInvalid = "ZALO_TOKEN_INVALID";
        public const string ZaloPhoneTokenInvalid = "ZALO_PHONE_TOKEN_INVALID";
        public const string ZaloPhoneNotFound = "ZALO_PHONE_NOT_FOUND";
        public const string ZaloApprovalPending = "ZALO_APPROVAL_PENDING";
        public const string ZaloRequestRejected = "ZALO_REQUEST_REJECTED";
        public const string ZaloAccountExists = "ZALO_ACCOUNT_EXISTS";
        public const string ZaloPhoneUnsupported = "ZALO_PHONE_UNSUPPORTED";
        public const string ZaloAlreadyLinked = "ZALO_ALREADY_LINKED";
        public const string ZaloNotLinked = "ZALO_NOT_LINKED";

        public const string LicenseReplaced = "LICENSE_REPLACED";

        /// <summary>
        /// Old codes still sent as <c>code</c>, because the SRS 27/10 catalogue has no message for the situation yet,
        /// or because the rule that sends them is replaced by a later PR of OMTS-216:
        /// <list type="bullet">
        /// <item>MSG05: an export file could not be generated, or a stored file could not be read (not an upload).</item>
        /// <item>MSG06: edit conflict (NFR-09, still cited by Part IV).</item>
        /// <item>MSG19: session expired.</item>
        /// <item>MSG21, MSG23: Email/Zalo channel and mandatory item of the notification preferences (PR 0.5 notifications v2).</item>
        /// <item>MSG24: the account is already linked to another employee (LABOR).</item>
        /// <item>MSG25, MSG26, MSG27: notification bodies (PR 0.5: MSG-FARM-01/02, MSG-WATER-06, MSG-WATER-03).</item>
        /// <item>MSG41: multiple of 1,000 VND (BR-PURCHASE-02 drops the rule in the PURCHASE PR).</item>
        /// <item>MSG45: receipt discount, paid-now and refund limits (fields not in the SRS; PURCHASE PR).</item>
        /// <item>MSG57: old documents hidden after a phone verification (PR 0.4, BR-ACCOUNT-04).</item>
        /// <item>MSG58: someone else is editing the record (NFR-13, still cited by Part IV).</item>
        /// <item>NOT_FOUND, TOO_MANY_REQUESTS (per-IP limit), UNEXPECTED_ERROR, PASSWORD_CHANGE_REQUIRED: technical codes.</item>
        /// </list>
        /// </summary>
        public static readonly IReadOnlySet<string> KeptAsCode = new HashSet<string>(StringComparer.Ordinal)
        {
            // Zalo lookup/service failures remain technical codes until the SRS assigns replacements.
            ZaloTokenInvalid, ZaloPhoneTokenInvalid, ZaloPhoneNotFound, ZaloApprovalPending, ZaloRequestRejected, ZaloAccountExists, ZaloPhoneUnsupported, ZaloAlreadyLinked, ZaloNotLinked,
            Msg04, Msg05, Msg06, Msg19, Msg21, Msg23, Msg24, Msg25, Msg26, Msg27, Msg41, Msg45, Msg57, Msg58,
            NotFound, TooManyRequests, UnexpectedError, PasswordChangeRequired, LicenseReplaced,
        };
    }
}
