using System.Text.Json;
using System.Text.RegularExpressions;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>
/// QA of OMTS-216 PR 0.5: the English text of each notification content is the SRS template filled in, and its parameters
/// are atomic values (QA finding #1: never an English phrase), so a client can render the same text in its own language.
/// </summary>
public sealed partial class NotificationMessageSrsTemplateQaTests
{
    // SRS 27/10 Part V message table (07-v-requirement-appendix.md, rows MSG-FARM-01/02, MSG-WATER-03/04/06), copied as data.
    private const string Cycle04 = "Raft {raft_code}: a cultivation log was recorded for the visit on {visit_date}. Please review it.";
    private const string Cycle05 = "Close the cultivation cycle of raft {raft_code}? After closing, the cycle cannot be reopened and its records can no longer be changed.";
    private const string Cycle06 = "Raft {raft_code}: the cultivation cycle was closed on {close_date} {early_note}. Seed hung: {seed_quantity} {seed_unit}. Harvested: about {kg_harvested} kg. Days of farming: {days}. Please review the summary.";
    private const string Cycle07 = "This cycle is not finished: {reason}. Oysters left on lanes that are Growing cannot be harvested in OMTS after the close. Closing it now needs the Cooperative Owner's approval. Has the Cooperative Owner approved closing it now?";
    private const string Cycle08 = "Raft {raft_code}: every seeded lane is harvested. Please close its cultivation cycle.";
    private const string Harvest01 = "Raft {raft_code}: the harvest of {harvest_date} is recorded as lot {lot_code} ({cage_count} cages, about {estimated_kg} kg).";
    private const string Harvest03 = "With {days} days, the next daily close check will close {count} lots that are still for sale. Do you want to save?";
    private const string Harvest05 = "Lot {lot_code} (raft {raft_code}) will be closed automatically at the start of {close_date}. Please sell it before then or close it yourself.";
    private const string Trace01 = "Harvest lot {lot_code} cannot be published yet: {missing_items}. The invoice is saved; the other lots are not affected.";
    private const string Trace02 = "Missing data in the trace of harvest lot {lot_code}: {missing_items}. Please ask the Cooperative Owner whether the data must be corrected.";
    private const string Trace03 ="The correction is not saved: lane {lane_number} of harvest lot {lot_code} would have no seed lot. Please keep at least one seed lot for this lane.";
    private const string Trace04 = "This correction changes the origin information of harvest lots {lot_codes}. Has the Cooperative Owner agreed in person that the data is wrong?";
    private const string Origin01 = "{cooperative_short_name} withdrew the origin information of lot {lot_code} on {withdrawal_date}. For details, please call {cooperative_phone}.";
    private const string Origin02 = "No origin information is shown for this invoice. For details, please call {cooperative_phone}.";
    private const string Origin04 = "This invoice has been cancelled, so no origin information is shown. For details, please call {cooperative_phone}.";
    private const string Trace05 = "{cooperative_short_name}: the origin information of oyster lot {lot_code} (invoice {invoice_codes}) has been withdrawn. You do not need to return the oysters. Please call {cooperative_phone}.";
    private const string Trace06 = "No current sales invoice sells harvest lot {lot_code}; no trader will be notified.";
    private const string Farm01 ="{document} expires on {expiry_date}, in {days} days. Please renew it and record the new one.";
    private const string Farm02 = "{document} expired on {expiry_date}. Please renew it and record the new one.";
    private const string Water03 = "Water alert {correction_note}: zone {zone_name}, raft {raft_code}, measured on {measurement_time}. {values_outside}. Please check the water.";
    private const string Water04 = "The water alert of zone {zone_name} (measured on {measurement_time}) was withdrawn: {user_name} {corrected_or_voided} the measurement. Reason: {reason}.";
    private const string Water06 ="Zone {zone_name} has had no water measurement since {last_date}. Please measure the water and record the result in OMTS.";

    private static readonly DateTimeOffset MeasuredAt = new(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);
    private static readonly WaterValueOutside SalinityLow = new(WaterValueOutside.Salinity, 12m, "‰", WaterValueDirection.Low, 15m, 30m);
    private static readonly WaterValueOutside PhHigh = new(WaterValueOutside.Ph, 9.1m, null, WaterValueDirection.High, 7.5m, 8.5m);

    public static TheoryData<string> Cases => new()
    {
        "farm-01", "farm-02", "farm-01-no-type", "farm-01-certification", "farm-02-certification-no-number", "water-03", "water-03-corrected", "water-04-corrected", "water-04-voided", "water-06",
        "cycle-04", "cycle-08", "harvest-01", "harvest-03", "harvest-05",
        "cycle-05", "cycle-06", "cycle-06-early", "cycle-07", "cycle-07-no-seeding",
        "trace-01", "trace-02", "trace-03", "trace-04", "trace-04-no-lot", "trace-05", "trace-06",
        "origin-01", "origin-01-no-phone", "origin-02", "origin-04",
    };

    // MSG-ORIGIN-03 has no placeholder: the text is the SRS text.
    [Fact]
    public void Msg_origin_03_is_the_srs_text()
    {
        Assert.Equal(
            ("MSG-ORIGIN-03", "No invoice was found for this QR code. Please scan the QR code on the invoice again, or open the link in the invoice message."),
            (UserMessages.ProvenanceUnknownQrCode.Code, UserMessages.ProvenanceUnknownQrCode.Text));
    }

    // MSG-TRACE-05 before the Owner saves a cooperative phone number (FARM-01): the trader is asked to call the cooperative.
    [Fact]
    public void Msg_trace_05_without_a_cooperative_phone_asks_to_call_the_cooperative()
    {
        var message = UserMessages.ProvenanceWithdrawnNotice("DEMO HTX", "KV01-B01-20261026", ["HD000001"], null);

        Assert.EndsWith("You do not need to return the oysters. Please call the cooperative.", message.Text);
        Assert.Null(message.Parameters!["cooperative_phone"]);
    }

    // TRACE-01.2: on the Trace lot screen there is no invoice, so the sentence about it is left out; the rest is the SRS text.
    [Fact]
    public void Msg_trace_01_on_the_trace_lot_screen_leaves_out_the_invoice_sentence()
    {
        var message = UserMessages.ProvenanceNotReady("KV01-B01-20261026", [OriginGap.NoSeedingSession(2)], atEndOfSale: false);

        Assert.Equal(("MSG-TRACE-01", "Harvest lot KV01-B01-20261026 cannot be published yet: lane 2 has no seeding session in the cycle of the lot."), (message.Code, message.Text));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_text_fills_the_srs_template_and_the_parameters_are_atomic(string name)
    {
        var (code, template, message, text, parameters) = Case(name);

        Assert.Equal(code, message.Code);
        Assert.Matches(TemplatePattern(template), message.Text);
        Assert.Equal(text, message.Text);
        var stored = JsonDocument.Parse(MessageParameterJson.Serialize(message.Parameters)!).RootElement;
        Assert.Equal(parameters, stored.GetRawText());
    }

    private static (string Code, string Template, UserMessage Message, string Text, string Parameters) Case(string name) => name switch
    {
        "farm-01" => (
            "MSG-FARM-01", Farm01,
            UserMessages.LicenseExpiringSoon("AQUACULTURE_LICENSE", "Marine aquaculture license", "GP-01", "KV02", new DateOnly(2026, 11, 29), 30),
            "Marine aquaculture license GP-01 (KV02) expires on 29/11/2026, in 30 days. Please renew it and record the new one.",
            """{"document_type":"AQUACULTURE_LICENSE","document_number":"GP-01","zone_codes":["KV02"],"expiry_date":"2026-11-29","days":30}"""),
        "farm-02" => (
            "MSG-FARM-02", Farm02,
            UserMessages.LicenseExpired("SEA_AREA_ALLOCATION", "Sea area allocation decision", "QĐ-7", "KV01", new DateOnly(2026, 11, 29)),
            "Sea area allocation decision QĐ-7 (KV01) expired on 29/11/2026. Please renew it and record the new one.",
            """{"document_type":"SEA_AREA_ALLOCATION","document_number":"QĐ-7","zone_codes":["KV01"],"expiry_date":"2026-11-29"}"""),
        // The license type is optional since PR 0.8.
        "farm-01-no-type" => (
            "MSG-FARM-01", Farm01,
            UserMessages.LicenseExpiringSoon(null, null, "GP-02", "KV01", new DateOnly(2026, 11, 1), 1),
            "Zone license GP-02 (KV01) expires on 01/11/2026, in 1 days. Please renew it and record the new one.",
            """{"document_type":null,"document_number":"GP-02","zone_codes":["KV01"],"expiry_date":"2026-11-01","days":1}"""),
        // {document} of a certification: its name and number (GFR-03).
        "farm-01-certification" => (
            "MSG-FARM-01", Farm01,
            UserMessages.CertificationExpiringSoon("VietGAP", "VG-01", new DateOnly(2026, 11, 29), 29),
            "VietGAP (VG-01) expires on 29/11/2026, in 29 days. Please renew it and record the new one.",
            """{"certification_name":"VietGAP","certification_number":"VG-01","expiry_date":"2026-11-29","days":29}"""),
        "farm-02-certification-no-number" => (
            "MSG-FARM-02", Farm02,
            UserMessages.CertificationExpired("Chứng nhận ATTP", null, new DateOnly(2026, 10, 30)),
            "Chứng nhận ATTP expired on 30/10/2026. Please renew it and record the new one.",
            """{"certification_name":"Chứng nhận ATTP","certification_number":null,"expiry_date":"2026-10-30"}"""),
        "water-03" => (
            "MSG-WATER-03", Water03,
            UserMessages.WaterAlert(false, "Vùng A", "BE-01", MeasuredAt, [SalinityLow]),
            "Water alert: zone Vùng A, raft BE-01, measured on 06:30 20/10/2026. Salinity 12 ‰ is too low (allowed 15–30 ‰). Please check the water.",
            """{"corrected":false,"zone_name":"Vùng A","raft_code":"BE-01","measurement_time":"2026-10-20T06:30:00+07:00","values_outside":[{"indicator":"SALINITY","value":12,"unit":"‰","direction":"Low","min":15,"max":30}]}"""),
        "water-03-corrected" => (
            "MSG-WATER-03", Water03,
            UserMessages.WaterAlert(true, "Vùng A", "BE-01", MeasuredAt, [PhHigh, SalinityLow]),
            "Water alert (corrected measurement): zone Vùng A, raft BE-01, measured on 06:30 20/10/2026. pH 9.1 is too high (allowed 7.5–8.5); Salinity 12 ‰ is too low (allowed 15–30 ‰). Please check the water.",
            """{"corrected":true,"zone_name":"Vùng A","raft_code":"BE-01","measurement_time":"2026-10-20T06:30:00+07:00","values_outside":[{"indicator":"PH","value":9.1,"unit":null,"direction":"High","min":7.5,"max":8.5},{"indicator":"SALINITY","value":12,"unit":"‰","direction":"Low","min":15,"max":30}]}"""),
        "water-04-corrected" => (
            "MSG-WATER-04", Water04,
            UserMessages.WaterAlertWithdrawn("Vùng A", MeasuredAt, "Nguyễn Văn A", "corrected", "Đọc nhầm số"),
            "The water alert of zone Vùng A (measured on 06:30 20/10/2026) was withdrawn: Nguyễn Văn A corrected the measurement. Reason: Đọc nhầm số.",
            """{"zone_name":"Vùng A","measurement_time":"2026-10-20T06:30:00+07:00","user_name":"Nguyễn Văn A","corrected_or_voided":"corrected","reason":"Đọc nhầm số"}"""),
        "water-04-voided" => (
            "MSG-WATER-04", Water04,
            UserMessages.WaterAlertWithdrawn("Vùng A", MeasuredAt, "Nguyễn Văn A", "voided", "Ghi nhầm vùng"),
            "The water alert of zone Vùng A (measured on 06:30 20/10/2026) was withdrawn: Nguyễn Văn A voided the measurement. Reason: Ghi nhầm vùng.",
            """{"zone_name":"Vùng A","measurement_time":"2026-10-20T06:30:00+07:00","user_name":"Nguyễn Văn A","corrected_or_voided":"voided","reason":"Ghi nhầm vùng"}"""),
        "cycle-05" => (
            "MSG-CYCLE-05", Cycle05,
            UserMessages.CloseCycleQuestion("KV01-B01"),
            "Close the cultivation cycle of raft KV01-B01? After closing, the cycle cannot be reopened and its records can no longer be changed.",
            """{"raft_code":"KV01-B01"}"""),
        // {early_note} is empty after a normal close, with the space before it.
        "cycle-06" => (
            "MSG-CYCLE-06", Cycle06,
            UserMessages.CycleClosedSummary("KV01-B01", new DateOnly(2026, 10, 29), false, [], 100m, "kg", 495.500m, 27),
            "Raft KV01-B01: the cultivation cycle was closed on 29/10/2026. Seed hung: 100 kg. Harvested: about 495.5 kg. Days of farming: 27. Please review the summary.",
            """{"raft_code":"KV01-B01","close_date":"2026-10-29","closed_early":false,"lane_numbers":[],"seed_quantity":100,"seed_unit":"kg","kg_harvested":495.5,"days":27}"""),
        "cycle-06-early" => (
            "MSG-CYCLE-06", Cycle06,
            UserMessages.CycleClosedSummary("KV01-B01", new DateOnly(2026, 10, 28), true, [9, 10], 100m, "kg", 450m, 26),
            "Raft KV01-B01: the cultivation cycle was closed on 28/10/2026 early; lanes not harvested: 9, 10. Seed hung: 100 kg. Harvested: about 450 kg. Days of farming: 26. Please review the summary.",
            """{"raft_code":"KV01-B01","close_date":"2026-10-28","closed_early":true,"lane_numbers":[9,10],"seed_quantity":100,"seed_unit":"kg","kg_harvested":450,"days":26}"""),
        "cycle-07" => (
            "MSG-CYCLE-07", Cycle07,
            UserMessages.EarlyCloseQuestion([10]),
            "This cycle is not finished: lanes 10 are not harvested. Oysters left on lanes that are Growing cannot be harvested in OMTS after the close. Closing it now needs the Cooperative Owner's approval. Has the Cooperative Owner approved closing it now?",
            """{"no_seeding":false,"lane_numbers":[10]}"""),
        "cycle-07-no-seeding" => (
            "MSG-CYCLE-07", Cycle07,
            UserMessages.EarlyCloseQuestion([]),
            "This cycle is not finished: no seeding session is recorded. Oysters left on lanes that are Growing cannot be harvested in OMTS after the close. Closing it now needs the Cooperative Owner's approval. Has the Cooperative Owner approved closing it now?",
            """{"no_seeding":true,"lane_numbers":[]}"""),
        "cycle-08" => (
            "MSG-CYCLE-08", Cycle08,
            UserMessages.CycleCloseReminder("KV01-B01"),
            "Raft KV01-B01: every seeded lane is harvested. Please close its cultivation cycle.",
            """{"raft_code":"KV01-B01"}"""),
        "harvest-01" => (
            "MSG-HARVEST-01", Harvest01,
            UserMessages.HarvestLotRecorded("KV01-B01", new DateOnly(2026, 10, 26), "KV01-B01-20261026", 8, 400.000m),
            "Raft KV01-B01: the harvest of 26/10/2026 is recorded as lot KV01-B01-20261026 (8 cages, about 400 kg).",
            """{"raft_code":"KV01-B01","harvest_date":"2026-10-26","lot_code":"KV01-B01-20261026","cage_count":8,"estimated_kg":400}"""),
        "harvest-03" => (
            "MSG-HARVEST-03", Harvest03,
            UserMessages.AutoCloseDaysLowered(2, 5),
            "With 2 days, the next daily close check will close 5 lots that are still for sale. Do you want to save?",
            """{"days":2,"count":5}"""),
        // GFR-05 example: harvested 26/10 with 3 days, warned by the check of 29/10.
        "harvest-05" => (
            "MSG-HARVEST-05", Harvest05,
            UserMessages.HarvestLotAutoCloseWarning("KV01-B01-20261026", "KV01-B01", new DateOnly(2026, 10, 30)),
            "Lot KV01-B01-20261026 (raft KV01-B01) will be closed automatically at the start of 30/10/2026. Please sell it before then or close it yourself.",
            """{"lot_code":"KV01-B01-20261026","raft_code":"KV01-B01","close_date":"2026-10-30"}"""),
        "cycle-04" => (
            "MSG-CYCLE-04", Cycle04,
            UserMessages.CultivationLogRecorded("KV01-B01", new DateOnly(2026, 10, 1)),
            "Raft KV01-B01: a cultivation log was recorded for the visit on 01/10/2026. Please review it.",
            """{"raft_code":"KV01-B01","visit_date":"2026-10-01"}"""),
        "trace-03" => (
            "MSG-TRACE-03", Trace03,
            UserMessages.LaneWithoutSeedLot(4, "KV01-B01-20261026"),
            "The correction is not saved: lane 4 of harvest lot KV01-B01-20261026 would have no seed lot. Please keep at least one seed lot for this lane.",
            """{"lane_number":4,"lot_code":"KV01-B01-20261026"}"""),
        // {missing_items}: each lane without a seed lot, joined with "; " (BR-TRACE-01); the parameter keeps the kind, lane, seed lots and zone.
        "trace-01" => (
            "MSG-TRACE-01", Trace01,
            UserMessages.ProvenanceNotReady(
                "KV01-B01-20261026-2", [OriginGap.NoSeedingSession(2), OriginGap.SeedLotNotOnCompletedReceipt(3, ["PN000004-1"]), OriginGap.NotPublisherOfZone("KV02")], atEndOfSale: true),
            "Harvest lot KV01-B01-20261026-2 cannot be published yet: lane 2 has no seeding session in the cycle of the lot; the seed lot PN000004-1 of lane 3 is not on a completed purchase receipt; only a Zone Manager of zone KV02 or the Cooperative Owner can publish it. The invoice is saved; the other lots are not affected.",
            """{"lot_code":"KV01-B01-20261026-2","missing_items":[{"kind":"NoSeedingSession","lane_number":2,"seed_lot_codes":[],"zone_code":null},{"kind":"SeedLotNotOnCompletedReceipt","lane_number":3,"seed_lot_codes":["PN000004-1"],"zone_code":null},{"kind":"NotPublisherOfZone","lane_number":null,"seed_lot_codes":[],"zone_code":"KV02"}]}"""),
        // TRACE-02.1: the same items as MSG-TRACE-01 (BR-TRACE-01), never filled in (BR-TRACE-03).
        "trace-02" => (
            "MSG-TRACE-02", Trace02,
            UserMessages.TraceIncomplete("KV01-B01-20261026", [OriginGap.NoSeedingSession(2), OriginGap.SeedLotNotOnCompletedReceipt(3, ["PN000004-1"])]),
            "Missing data in the trace of harvest lot KV01-B01-20261026: lane 2 has no seeding session in the cycle of the lot; the seed lot PN000004-1 of lane 3 is not on a completed purchase receipt. Please ask the Cooperative Owner whether the data must be corrected.",
            """{"lot_code":"KV01-B01-20261026","missing_items":[{"kind":"NoSeedingSession","lane_number":2,"seed_lot_codes":[],"zone_code":null},{"kind":"SeedLotNotOnCompletedReceipt","lane_number":3,"seed_lot_codes":["PN000004-1"],"zone_code":null}]}"""),
        // ORIGIN-01.1: the withdrawal date (business date), never the reason (BR-TRACE-06).
        "origin-01" => (
            "MSG-ORIGIN-01", Origin01,
            UserMessages.ProvenanceLotWithdrawn("DEMO HTX", "KV01-B01-20261026", new DateOnly(2026, 11, 8), "DEMO-PHONE"),
            "DEMO HTX withdrew the origin information of lot KV01-B01-20261026 on 08/11/2026. For details, please call DEMO-PHONE.",
            """{"cooperative_short_name":"DEMO HTX","lot_code":"KV01-B01-20261026","withdrawal_date":"2026-11-08","cooperative_phone":"DEMO-PHONE"}"""),
        // A profile saved before the phone number became mandatory: the text shows "–", the parameter stays null.
        "origin-01-no-phone" => (
            "MSG-ORIGIN-01", Origin01,
            UserMessages.ProvenanceLotWithdrawn("DEMO HTX", "KV01-B01-20261026", new DateOnly(2026, 11, 8), null),
            "DEMO HTX withdrew the origin information of lot KV01-B01-20261026 on 08/11/2026. For details, please call –.",
            """{"cooperative_short_name":"DEMO HTX","lot_code":"KV01-B01-20261026","withdrawal_date":"2026-11-08","cooperative_phone":null}"""),
        "origin-02" => (
            "MSG-ORIGIN-02", Origin02,
            UserMessages.ProvenanceNoLots("DEMO-PHONE"),
            "No origin information is shown for this invoice. For details, please call DEMO-PHONE.",
            """{"cooperative_phone":"DEMO-PHONE"}"""),
        "origin-04" => (
            "MSG-ORIGIN-04", Origin04,
            UserMessages.ProvenanceInvoiceCancelled("DEMO-PHONE"),
            "This invoice has been cancelled, so no origin information is shown. For details, please call DEMO-PHONE.",
            """{"cooperative_phone":"DEMO-PHONE"}"""),
        "trace-04" => (
            "MSG-TRACE-04", Trace04,
            UserMessages.TraceCorrectionAgreement(["KV01-B01-20261026", "KV01-B01-20261026-2"]),
            "This correction changes the origin information of harvest lots KV01-B01-20261026, KV01-B01-20261026-2. Has the Cooperative Owner agreed in person that the data is wrong?",
            """{"lot_codes":["KV01-B01-20261026","KV01-B01-20261026-2"]}"""),
        // A correction that changes no lot (a cultivation log, BR-TRACE-05) leaves out the sentence naming the lots.
        "trace-04-no-lot" => (
            "MSG-TRACE-04", Trace04,
            UserMessages.TraceCorrectionAgreement([]),
            "Has the Cooperative Owner agreed in person that the data is wrong?",
            """{"lot_codes":[]}"""),
        // BR-TRACE-07: one notice per trader, listing their invoices, never the reason.
        "trace-05" => (
            "MSG-TRACE-05", Trace05,
            UserMessages.ProvenanceWithdrawnNotice("DEMO HTX", "KV01-B01-20261026", ["HD000001", "HD000004"], "0900000099"),
            "DEMO HTX: the origin information of oyster lot KV01-B01-20261026 (invoice HD000001, HD000004) has been withdrawn. You do not need to return the oysters. Please call 0900000099.",
            """{"cooperative_short_name":"DEMO HTX","lot_code":"KV01-B01-20261026","invoice_codes":["HD000001","HD000004"],"cooperative_phone":"0900000099"}"""),
        "trace-06" => (
            "MSG-TRACE-06", Trace06,
            UserMessages.NoTraderToNotify("KV01-B01-20261026"),
            "No current sales invoice sells harvest lot KV01-B01-20261026; no trader will be notified.",
            """{"lot_code":"KV01-B01-20261026"}"""),
        "water-06" => (
            "MSG-WATER-06", Water06,
            UserMessages.WaterMeasurementOverdue("Vùng A", new DateOnly(2026, 10, 1)),
            "Zone Vùng A has had no water measurement since 01/10/2026. Please measure the water and record the result in OMTS.",
            """{"zone_name":"Vùng A","last_date":"2026-10-01"}"""),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    // Each placeholder stands for some text; an empty {correction_note} also drops the space before it ("Water alert: zone …").
    // So does an empty {early_note} of MSG-CYCLE-06 ("closed on 29/10/2026. Seed hung …"), and MSG-TRACE-04 with no lot drops its first sentence.
    private static string TemplatePattern(string template) =>
        "^" + Placeholder().Replace(
            Regex.Escape(template)
                .Replace(@"\ \{correction_note}", "( .+)?", StringComparison.Ordinal)
                .Replace(@"\ \{early_note}", "( .+)?", StringComparison.Ordinal)
                .Replace(@"\ lots\ \{lot_codes}\.\ ", @"\ lots\ .+\.\ ", StringComparison.Ordinal)
                .Replace(@"This\ correction\ changes\ the\ origin\ information\ of\ harvest\ lots\ .+\.\ ", @"(This\ correction\ changes\ the\ origin\ information\ of\ harvest\ lots\ .+\.\ )?", StringComparison.Ordinal),
            ".+") + "$";

    [GeneratedRegex(@"\\\{[a-z_]+}")]
    private static partial Regex Placeholder();
}
