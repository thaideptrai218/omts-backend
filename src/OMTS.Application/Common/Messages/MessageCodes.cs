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
    public const string Gen11 = "MSG-GEN-11";

    public const string Farm01 = "MSG-FARM-01";
    public const string Farm02 = "MSG-FARM-02";

    public const string Cycle01 = "MSG-CYCLE-01";
    public const string Cycle02 = "MSG-CYCLE-02";
    public const string Cycle03 = "MSG-CYCLE-03";
    public const string Cycle04 = "MSG-CYCLE-04";
    public const string Cycle05 = "MSG-CYCLE-05";
    public const string Cycle06 = "MSG-CYCLE-06";
    public const string Cycle07 = "MSG-CYCLE-07";
    public const string Cycle08 = "MSG-CYCLE-08";

    public const string Harvest01 = "MSG-HARVEST-01";
    public const string Harvest03 = "MSG-HARVEST-03";

    public const string Trace03 = "MSG-TRACE-03";
    public const string Trace04 = "MSG-TRACE-04";

    public const string Water01 = "MSG-WATER-01";
    public const string Water02 = "MSG-WATER-02";
    public const string Water03 = "MSG-WATER-03";
    public const string Water04 = "MSG-WATER-04";
    public const string Water05 = "MSG-WATER-05";
    public const string Water06 = "MSG-WATER-06";

    public const string Account01 = "MSG-ACCOUNT-01";
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
    public const string Account22 = "MSG-ACCOUNT-22";
    public const string Account23 = "MSG-ACCOUNT-23";

    public const string Purchase01 = "MSG-PURCHASE-01";
    public const string Purchase02 = "MSG-PURCHASE-02";
    public const string Purchase03 = "MSG-PURCHASE-03";
    public const string Purchase04 = "MSG-PURCHASE-04";
    public const string Purchase05 = "MSG-PURCHASE-05";
    public const string Purchase06 = "MSG-PURCHASE-06";
    public const string Purchase07 = "MSG-PURCHASE-07";
    public const string Purchase08 = "MSG-PURCHASE-08";
    public const string Purchase09 = "MSG-PURCHASE-09";
    public const string Purchase10 = "MSG-PURCHASE-10";

    public const string Labor01 = "MSG-LABOR-01";
    public const string Labor02 = "MSG-LABOR-02";
    public const string Labor03 = "MSG-LABOR-03";
    public const string Labor04 = "MSG-LABOR-04";
    public const string Labor36 = "MSG-LABOR-36";

    public const string Debt01 = "MSG-DEBT-01";
    public const string Debt02 = "MSG-DEBT-02";

    public const string Sale01 = "MSG-SALE-01";
    public const string Sale02 = "MSG-SALE-02";
    public const string Sale03 = "MSG-SALE-03";
    public const string Sale04 = "MSG-SALE-04";
    public const string Sale05 = "MSG-SALE-05";
    public const string Sale06 = "MSG-SALE-06";
    public const string Sale07 = "MSG-SALE-07";
    public const string Sale08 = "MSG-SALE-08";
    public const string Sale10 = "MSG-SALE-10";
    public const string Sale11 = "MSG-SALE-11";
    public const string Sale12 = "MSG-SALE-12";
    public const string Sale15 = "MSG-SALE-15";
    public const string Sale16 = "MSG-SALE-16";

    /// <summary>
    /// SRS 27/10 codes for situations that had no message before, so they name no legacy code: MSG-ACCOUNT-01, the
    /// account is linked to the existing customer profile; MSG-ACCOUNT-22, the record of a notification or of an audit
    /// event no longer exists (ACCOUNT-08 and ACCOUNT-10 are new in OMTS-216); MSG-PURCHASE-01/07/08, the purchase receipt
    /// notifications and the advance warning, and MSG-PURCHASE-10, the Owner's correction (OMTS-218 P2); MSG-PURCHASE-04/05/06,
    /// the transaction notification and the two payment confirmations (OMTS-218 P3); MSG-PURCHASE-02, the confirmation of the
    /// Owner's approval of a return (OMTS-218 P4); MSG-LABOR-01 and MSG-LABOR-02, the approval of a new employee and the
    /// reminder to lock a staff account, MSG-LABOR-04 and MSG-LABOR-36, the pay period lock and the question before saving
    /// the payroll settings (OMTS-227); MSG-SALE-04 and MSG-SALE-05, the merge of customer profiles (SALE-04 is
    /// new); MSG-DEBT-01 and MSG-DEBT-02, the receiving bank account (DEBT-01); MSG-GEN-11, MSG-SALE-01, MSG-SALE-02 and
    /// MSG-SALE-03, the purchase requests (new in PR S4); MSG-CYCLE-01/02/03, the seeding questions and the notice (CYCLE-02);
    /// MSG-CYCLE-04, a cultivation log to review (CYCLE-04); MSG-CYCLE-08, MSG-HARVEST-01/03, the reminder to close a cycle,
    /// the harvest notice and the lowered days (HARVEST-01 and HARVEST-03 are new in PR H1a); MSG-CYCLE-05/06/07, the close of a
    /// cycle, its summary notice and the early close (CYCLE-06 is new in PR C5); MSG-TRACE-03/04, a lane left with no seed lot and the
    /// Owner's agreement to a correction (TRACE-03 is new in PR T2); MSG-SALE-06/08/10/11/12, the sales invoice: its success text, the
    /// Owner's notice and the three questions at the port (SALE-05 is new in PR 6a); MSG-SALE-07/15/16, the QR question at the end of the sale, the
    /// invoice message to the trader and the invoice not sent (SALE-06 is new in PR 6b).
    /// </summary>
    public static readonly IReadOnlySet<string> NewWithoutLegacyCode = new HashSet<string>(StringComparer.Ordinal)
    {
        Account01, Account22, Cycle01, Cycle02, Cycle03, Cycle04, Cycle05, Cycle06, Cycle07, Cycle08, Harvest01, Harvest03, Trace03, Trace04, Gen11, Purchase01, Purchase02, Purchase04, Purchase05, Purchase06, Purchase07, Purchase08, Purchase10, Labor01, Labor02, Labor04, Labor36, Sale01, Sale02, Sale03, Sale04, Sale05, Sale06, Sale07, Sale08, Sale10, Sale11, Sale12, Sale15, Sale16, Debt01, Debt02, Water02, Water04,
    };

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
        public const string Msg22 = "MSG22";
        public const string Msg25 = "MSG25";
        public const string Msg26 = "MSG26";
        public const string Msg27 = "MSG27";
        public const string Msg28 = "MSG28";
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
        public const string LicenseReplaced = "LICENSE_REPLACED";

        /// <summary>
        /// Old codes still sent as <c>code</c>, because the SRS 27/10 catalogue has no message for the situation yet,
        /// or because the rule that sends them is replaced by a later PR of OMTS-216:
        /// <list type="bullet">
        /// <item>MSG05: an export file could not be generated, or a stored file could not be read (not an upload).</item>
        /// <item>MSG06: edit conflict (NFR-09, still cited by Part IV).</item>
        /// <item>MSG19: session expired.</item>
        /// <item>MSG58: someone else is editing the record (NFR-13, still cited by Part IV).</item>
        /// <item>NOT_FOUND, TOO_MANY_REQUESTS (per-IP limit), UNEXPECTED_ERROR, PASSWORD_CHANGE_REQUIRED: technical codes.</item>
        /// </list>
        /// </summary>
        public static readonly IReadOnlySet<string> KeptAsCode = new HashSet<string>(StringComparer.Ordinal)
        {
            Msg05, Msg06, Msg19, Msg58,
            NotFound, TooManyRequests, UnexpectedError, PasswordChangeRequired,
        };
    }
}
