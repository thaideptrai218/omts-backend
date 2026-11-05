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
    // SRS 27/10 Part V message table (07-v-requirement-appendix.md, rows MSG-FARM-01/02, MSG-WATER-03/06), copied as data.
    private const string Farm01 = "{document} expires on {expiry_date}, in {days} days. Please renew it and record the new one.";
    private const string Farm02 = "{document} expired on {expiry_date}. Please renew it and record the new one.";
    private const string Water03 = "Water alert {correction_note}: zone {zone_name}, raft {raft_code}, measured on {measurement_time}. {values_outside}. Please check the water.";
    private const string Water06 = "Zone {zone_name} has had no water measurement since {last_date}. Please measure the water and record the result in OMTS.";

    private static readonly DateTimeOffset MeasuredAt = new(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);
    private static readonly WaterValueOutside SalinityLow = new(WaterValueOutside.Salinity, 12m, "‰", WaterValueDirection.Low, 15m, 30m);
    private static readonly WaterValueOutside PhHigh = new(WaterValueOutside.Ph, 9.1m, null, WaterValueDirection.High, 7.5m, 8.5m);

    public static TheoryData<string> Cases => new() { "farm-01", "farm-02", "water-03", "water-03-corrected", "water-06" };

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
            UserMessages.LicenseExpiringSoon("AQUACULTURE_LICENSE", "Marine aquaculture license", "GP-01", ["KV01", "KV02"], new DateOnly(2026, 11, 29), 30),
            "Marine aquaculture license GP-01 (KV01, KV02) expires on 29/11/2026, in 30 days. Please renew it and record the new one.",
            """{"document_type":"AQUACULTURE_LICENSE","document_number":"GP-01","zone_codes":["KV01","KV02"],"expiry_date":"2026-11-29","days":30}"""),
        "farm-02" => (
            "MSG-FARM-02", Farm02,
            UserMessages.LicenseExpired("SEA_AREA_ALLOCATION", "Sea area allocation decision", "QĐ-7", ["KV01"], new DateOnly(2026, 11, 29)),
            "Sea area allocation decision QĐ-7 (KV01) expired on 29/11/2026. Please renew it and record the new one.",
            """{"document_type":"SEA_AREA_ALLOCATION","document_number":"QĐ-7","zone_codes":["KV01"],"expiry_date":"2026-11-29"}"""),
        "water-03" => (
            "MSG-WATER-03", Water03,
            UserMessages.WaterAlert(false, "Vùng A", null, MeasuredAt, [SalinityLow]),
            "Water alert: zone Vùng A, raft –, measured on 06:30 20/10/2026. Salinity 12 ‰ is too low (allowed 15–30 ‰). Please check the water.",
            """{"corrected":false,"zone_name":"Vùng A","raft_code":null,"measurement_time":"2026-10-20T06:30:00+07:00","values_outside":[{"indicator":"SALINITY","value":12,"unit":"‰","direction":"Low","min":15,"max":30}]}"""),
        "water-03-corrected" => (
            "MSG-WATER-03", Water03,
            UserMessages.WaterAlert(true, "Vùng A", "BE-01", MeasuredAt, [PhHigh, SalinityLow]),
            "Water alert (corrected measurement): zone Vùng A, raft BE-01, measured on 06:30 20/10/2026. pH 9.1 is too high (allowed 7.5–8.5); Salinity 12 ‰ is too low (allowed 15–30 ‰). Please check the water.",
            """{"corrected":true,"zone_name":"Vùng A","raft_code":"BE-01","measurement_time":"2026-10-20T06:30:00+07:00","values_outside":[{"indicator":"PH","value":9.1,"unit":null,"direction":"High","min":7.5,"max":8.5},{"indicator":"SALINITY","value":12,"unit":"‰","direction":"Low","min":15,"max":30}]}"""),
        "water-06" => (
            "MSG-WATER-06", Water06,
            UserMessages.WaterMeasurementOverdue("Vùng A", new DateOnly(2026, 10, 1)),
            "Zone Vùng A has had no water measurement since 01/10/2026. Please measure the water and record the result in OMTS.",
            """{"zone_name":"Vùng A","last_date":"2026-10-01"}"""),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    // Each placeholder stands for some text; an empty {correction_note} also drops the space before it ("Water alert: zone …").
    private static string TemplatePattern(string template) =>
        "^" + Placeholder().Replace(Regex.Escape(template).Replace(@"\ \{correction_note}", "( .+)?", StringComparison.Ordinal), ".+") + "$";

    [GeneratedRegex(@"\\\{[a-z_]+}")]
    private static partial Regex Placeholder();
}
