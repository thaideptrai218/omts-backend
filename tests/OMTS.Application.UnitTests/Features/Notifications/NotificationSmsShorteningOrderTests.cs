using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>
/// The one order in which a text gives way at 160 characters (<see cref="NotificationSmsText"/>): the free text to its
/// short minimum, the closing sentence, the shorter wording, the free text below its minimum. The values, the allowed
/// ranges, the time and the expiry date never give way.
/// </summary>
public class NotificationSmsShorteningOrderTests
{
    private const string Time = "06:30 20/10/2026";
    private const string Closing = "Vui long kiem tra nuoc.";
    private const string RenewIt = "Please renew it and record the new one.";

    private static readonly DateTimeOffset MeasuredAt = new(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Expiry = new(2026, 11, 29);
    private static readonly WaterValueOutside Ph = new(WaterValueOutside.Ph, 9.1m, null, WaterValueDirection.High, 6.5m, 8.5m);
    private static readonly WaterValueOutside Salinity = new(WaterValueOutside.Salinity, 12m, "‰", WaterValueDirection.Low, 15m, 30m);

    private static string Alert(string zone, string? raft, bool corrected, params WaterValueOutside[] values) => Render(
        NotificationTypes.WaterAlert, UserMessages.WaterAlert(corrected, zone, raft ?? "-", MeasuredAt, values));

    private static string Soon(string number, IReadOnlyList<string> zones) => Render(
        NotificationTypes.ZoneLicenseExpiry, UserMessages.LicenseExpiringSoon("AQUACULTURE_LICENSE", "Marine aquaculture license", number, string.Join(", ", zones), Expiry, 30));

    private static string Render(string type, UserMessage message)
    {
        var definition = NotificationTypes.Find(type)!;
        return NotificationSmsText.Render(definition, definition.Sms!, new NotificationContent(type, "Title", message));
    }

    private static List<string> Zones(int count) => Enumerable.Range(1, count).Select(i => $"KV-HL-{i:00}").ToList();

    // ---- Step 1: the free text first ----

    [Fact]
    public void A_long_zone_name_gives_way_and_everything_else_stays()
    {
        var text = Alert(string.Concat(Enumerable.Repeat("Vùng nuôi hàu ", 15)), null, false, Ph);

        Assert.Equal(
            $"Canh bao nuoc: vung Vung nuoi hau Vung nuoi hau Vung nuoi hau Vung..., be -, do luc {Time}. pH 9,1 cao hon muc cho phep 8,5. {Closing}",
            text);
    }

    [Fact]
    public void A_zone_name_gives_way_before_the_raft_code()
    {
        var text = Alert(new string('A', 200), "BE-0012", false, Ph);

        Assert.Contains(", be BE-0012, do luc", text);
        Assert.EndsWith(Closing, text);
        Assert.Equal(NotificationSmsText.MaxLength, text.Length);
    }

    [Fact]
    public void A_raft_code_gives_way_once_the_zone_name_is_at_its_minimum()
    {
        var text = Alert(new string('A', 200), new string('B', 50), false, Ph);

        Assert.Contains(", be BBB", text);
        Assert.Contains("vung AAAAAAAAA..., be", text);
        Assert.EndsWith(Closing, text);
        Assert.Equal(NotificationSmsText.MaxLength, text.Length);
    }

    [Fact]
    public void A_zone_list_gives_way_first_and_stops_at_a_whole_code()
    {
        var text = Soon("GP-01", Zones(12));

        Assert.Equal(
            $"Marine aquaculture license GP-01 (KV-HL-01, KV-HL-02, KV-HL-03, KV-HL-04...) expires on 29/11/2026, in 30 days. {RenewIt}",
            text);
    }

    [Fact]
    public void A_license_type_name_gives_way_after_the_zone_list_and_before_the_license_number()
    {
        var number = new string('9', 45);

        var text = Soon(number, Zones(40));

        Assert.Contains($"Marine aquaculture... {number} (KV-HL-01...) expires on 29/11/2026, in 30 days.", text);
        Assert.EndsWith(RenewIt, text);
    }

    [Fact]
    public void A_license_number_gives_way_last_of_the_free_text()
    {
        var text = Soon(new string('9', 100), Zones(40));

        Assert.Matches(@"^Marine... 9+\.\.\. \(KV-HL-01\.\.\.\) expires on 29/11/2026, in 30 days\. Please renew", text);
        Assert.True(text.Length <= NotificationSmsText.MaxLength, text);
    }

    // ---- Step 2: then the closing sentence, and the free text takes back what that leaves ----

    [Fact]
    public void Two_values_lose_the_closing_sentence_and_keep_as_much_of_the_name_as_then_fits()
    {
        var text = Alert("Vùng nuôi hàu Vịnh Bái Tử Long khu số 2", null, false, Ph, Salinity);

        Assert.Equal(
            $"Canh bao nuoc: vung Vung nuoi hau Vinh Bai Tu..., be -, do luc {Time}. pH 9,1 cao hon muc cho phep 8,5; Do man 12 ppt thap hon muc cho phep 15 ppt.",
            text);
    }

    [Fact]
    public void An_expired_license_with_the_longest_number_loses_the_closing_sentence_last()
    {
        var text = Render(
            NotificationTypes.ZoneLicenseExpiry,
            UserMessages.LicenseExpired("AQUACULTURE_LICENSE", "Marine aquaculture license", new string('9', 100), string.Join(", ", Zones(40)), Expiry));

        Assert.Contains("expired on 29/11/2026.", text);
        Assert.True(text.Length <= NotificationSmsText.MaxLength, text);
    }

    // ---- Step 3: then the shorter wording, which keeps every fact ----

    [Fact]
    public void A_corrected_two_value_alert_takes_the_shorter_wording_and_keeps_the_ordinary_name_whole()
    {
        var text = Alert("Vùng Cái Rồng", null, true, Ph, Salinity);

        Assert.Equal($"Canh bao nuoc (da hieu chinh): vung Vung Cai Rong, be -, luc {Time}. pH 9,1 cao hon 8,5; Do man 12 ppt thap hon 15 ppt.", text);
    }

    // ---- Step 4: the worst case of the columns. The free text goes under its minimum and no fact is lost ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_widest_values_of_the_columns_with_the_longest_free_text_lose_no_fact(bool corrected)
    {
        var ph = new WaterValueOutside(WaterValueOutside.Ph, 99.75m, null, WaterValueDirection.High, 12.25m, 13.75m);
        var salinity = new WaterValueOutside(WaterValueOutside.Salinity, 999.25m, "‰", WaterValueDirection.High, 100.25m, 999.75m);

        var text = Alert(new string('A', 200), new string('B', 50), corrected, ph, salinity);

        Assert.True(text.Length <= NotificationSmsText.MaxLength, text);
        Assert.Contains(Time, text);
        Assert.Contains("pH 99,75 cao hon ", text);
        Assert.Contains(" 13,75;", text);
        Assert.Contains("Do man 999,25 ppt cao hon ", text);
        Assert.Contains(" 999,75 ppt.", text);
    }

    [Fact]
    public void Limits_set_on_one_side_only_lose_no_fact_either()
    {
        var ph = new WaterValueOutside(WaterValueOutside.Ph, 99.75m, null, WaterValueDirection.Low, 13.75m, null);
        var salinity = new WaterValueOutside(WaterValueOutside.Salinity, 999.25m, "‰", WaterValueDirection.High, null, 999.75m);

        var text = Alert(new string('A', 200), new string('B', 50), true, ph, salinity);

        Assert.True(text.Length <= NotificationSmsText.MaxLength, text);
        Assert.Contains("pH 99,75 thap hon 13,75;", text);
        Assert.Contains("Do man 999,25 ppt cao hon 999,75 ppt.", text);
    }

    [Fact]
    public void A_text_that_fits_is_the_message_text_with_nothing_given_up()
    {
        var name = new string('A', 41);

        Assert.Equal(
            $"Canh bao nuoc: vung {name}, be -, do luc {Time}. pH 9,1 cao hon muc cho phep 8,5. {Closing}",
            Alert(name, null, false, Ph));
    }
}
