using System.Text.RegularExpressions;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>
/// QA of OMTS-216 PR 0.5: each notification content keeps the SRS placeholders as its parameters (by their SRS names)
/// and its English text is the SRS template filled with them, so a client can render the same text from the code.
/// </summary>
public sealed partial class NotificationMessageSrsTemplateQaTests
{
    // SRS 27/10 Part V message table (07-v-requirement-appendix.md, rows MSG-FARM-01/02, MSG-WATER-03/06), copied as data.
    private const string Farm01 = "{document} expires on {expiry_date}, in {days} days. Please renew it and record the new one.";
    private const string Farm02 = "{document} expired on {expiry_date}. Please renew it and record the new one.";
    private const string Water03 = "Water alert {correction_note}: zone {zone_name}, raft {raft_code}, measured on {measurement_time}. {values_outside}. Please check the water.";
    private const string Water06 = "Zone {zone_name} has had no water measurement since {last_date}. Please measure the water and record the result in OMTS.";

    public static TheoryData<string> Cases => new() { "farm-01", "farm-02", "water-03", "water-03-corrected", "water-06" };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_parameters_are_the_srs_placeholders_and_fill_the_srs_text(string name)
    {
        var (code, template, message) = Case(name);

        Assert.Equal(code, message.Code);
        var parameters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(message.Parameters);
        Assert.Equal(Placeholder().Matches(template).Select(m => m.Groups[1].Value).Order(), parameters.Keys.Order());
        var filled = Placeholder().Replace(template, m => parameters[m.Groups[1].Value]);
        // An empty {correction_note} leaves no space before the colon: "Water alert: zone …".
        Assert.Equal(filled.Replace(" :", ":", StringComparison.Ordinal), message.Text);
        Assert.DoesNotContain("{", message.Text, StringComparison.Ordinal);
    }

    private static (string Code, string Template, UserMessage Message) Case(string name) => name switch
    {
        "farm-01" => ("MSG-FARM-01", Farm01, UserMessages.LicenseExpiringSoon("Marine aquaculture license GP-01 (Vùng A)", "29/11/2026", 30)),
        "farm-02" => ("MSG-FARM-02", Farm02, UserMessages.LicenseExpired("Marine aquaculture license GP-01 (Vùng A)", "29/11/2026")),
        "water-03" => ("MSG-WATER-03", Water03, UserMessages.WaterAlert("", "Vùng A", "–", "06:30 20/10/2026", "Salinity 12 ‰ is too low (allowed 15–30 ‰)")),
        "water-03-corrected" => ("MSG-WATER-03", Water03, UserMessages.WaterAlert("(corrected measurement)", "Vùng A", "–", "06:30 20/10/2026", "pH 9.1 is too high (allowed 7.5–8.5)")),
        "water-06" => ("MSG-WATER-06", Water06, UserMessages.WaterMeasurementOverdue("Vùng A", "01/10/2026")),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex Placeholder();
}
