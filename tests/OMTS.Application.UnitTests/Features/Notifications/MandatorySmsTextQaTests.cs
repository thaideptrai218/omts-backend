using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>
/// QA of the SMS text (decision 2 of OMTS-216, BR-WATER-05, MSG-WATER-03, MSG-FARM-01). Tests named M.. or L.. each name a
/// finding of reports/qa-261105-mandatory-sms.md; the ones that wait for the PM are skipped and say why. The others state
/// what was checked and holds.
/// </summary>
public class MandatorySmsTextQaTests
{
    // Not a real account number: built from a pattern, so no account-like literal is committed.
    private static readonly string Account = string.Concat(Enumerable.Range(1, 13).Select(i => i % 10));
    private static readonly DateTimeOffset MeasuredAt = new(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);

    private static readonly NotificationTypeDefinition MoneyType = new(
        "SUPPLIER_TRANSACTION", "Supplier payment transaction", NotificationPushPolicy.AlwaysPush, [NotificationRole.CooperativeOwner],
        NotificationOpenAccess.OwnerOnly, NotificationSmsRule.MoneyToOwner("amount"));

    private static string Grouped(string separator) => string.Join(separator, Account.Chunk(4).Select(c => new string(c)));

    private static string Digits(string text) => new(text.Where(char.IsAsciiDigit).ToArray());

    private static string AlertWithZone(string zone, params WaterValueOutside[] values) => Render(
        NotificationTypes.WaterAlert, new NotificationContent(NotificationTypes.WaterAlert, "Title", UserMessages.WaterAlert(false, zone, null, MeasuredAt, values)));

    private static string Render(string type, NotificationContent content)
    {
        var definition = NotificationTypes.Find(type)!;
        return NotificationSmsText.Render(definition, definition.Sms!, content);
    }

    private static string RenderMoney(string? code, object? amount) => NotificationSmsText.Render(
        MoneyType,
        MoneyType.Sms!,
        new NotificationContent(
            MoneyType.Code,
            "Title",
            new UserMessage("MSG-PURCHASE-04", $"Text with account {Account}") { Parameters = new Dictionary<string, object?> { ["amount"] = amount } },
            code is null ? null : new RelatedRecord("SupplierPayment", Guid.NewGuid(), code)));

    private static WaterValueOutside Ph(decimal value = 9.1m) => new(WaterValueOutside.Ph, value, null, WaterValueDirection.High, 6.5m, 8.5m);

    private static WaterValueOutside Salinity(decimal value = 12m, decimal min = 15m, decimal max = 30m) =>
        new(WaterValueOutside.Salinity, value, "‰", WaterValueDirection.Low, min, max);

    // ---- M1: numbers in a free-text zone name. The mask of the operational texts is gone (M1 to M3 are one decision). ----

    [Theory(Skip = "Waiting for the PM: may a number typed into a zone name reach an SMS? Only money SMS are held to code and amount.")]
    [InlineData(" - ")]
    [InlineData(" / ")]
    [InlineData("/")]
    [InlineData(",")]
    [InlineData(", ")]
    [InlineData("_")]
    [InlineData(". ")]
    [InlineData("--")]
    [InlineData(":")]
    [InlineData(";")]
    public void M1_a_bank_account_number_grouped_in_any_way_never_reaches_the_phone(string separator)
    {
        var text = AlertWithZone($"Khu A TK {Grouped(separator)}", Ph());

        Assert.False(Digits(text).Contains(Account), text);
    }

    // EmployeeInput on feature/OMTS-223-bank-account accepts an account number of 6 to 20 digits.
    [Theory(Skip = "Waiting for the PM: may a number typed into a zone name reach an SMS? Only money SMS are held to code and amount.")]
    [InlineData("765432")]
    [InlineData("7654321")]
    public void M1_a_short_bank_account_number_never_reaches_the_phone(string number)
    {
        var text = AlertWithZone($"Khu A TK {number}", Ph());

        Assert.False(text.Contains(number), text);
    }

    // Asks the same question as M1: it passed only through the mask.
    [Theory(Skip = "Waiting for the PM: may a number typed into a zone name reach an SMS? Only money SMS are held to code and amount.")]
    [InlineData(" ")]
    [InlineData("  ")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData(" ")] // no-break space
    [InlineData(" ")] // thin space
    [InlineData("​")] // zero-width space
    [InlineData("–")] // en dash
    [InlineData("—")] // em dash
    [InlineData("−")] // minus
    [InlineData("‐")] // hyphen
    [InlineData("́")] // a combining mark
    [InlineData("🦪")]
    [InlineData("")]
    public void The_mask_catches_the_groupings_the_client_list_promises(string separator)
    {
        var text = AlertWithZone($"Khu A TK {Grouped(separator)}", Ph());

        Assert.DoesNotContain(Account, Digits(text));
        Assert.Contains("***", text);
    }

    [Fact]
    public void Digits_of_other_scripts_never_reach_the_phone_at_all()
    {
        var fullWidth = string.Concat(Account.Select(c => (char)(c - '0' + 0xFF10)));
        var arabic = string.Concat(Account.Select(c => (char)(c - '0' + 0x0660)));

        var text = AlertWithZone($"{fullWidth} {arabic}", Ph());

        Assert.DoesNotContain(Account, Digits(text));
        Assert.All(text, c => Assert.Contains(c, NotificationSmsText.Alphabet));
    }

    // Asks the same question as M1: it passed only through the mask.
    [Theory(Skip = "Waiting for the PM: may a number typed into a zone name reach an SMS? Only money SMS are held to code and amount.")]
    [InlineData("Nguyễn Văn 12345678")]
    [InlineData("12345678 Trần")]
    public void Digits_in_a_name_are_masked_as_any_other(string zone)
    {
        var text = AlertWithZone(zone, Ph());

        Assert.DoesNotContain("12345678", text);
    }

    // ---- M2, M3: what the alert must name reads as it is ----

    // BR-WATER-05: the alert names "each value outside, with its allowed range"; the columns hold two decimals.
    [Fact]
    public void M2_a_water_alert_keeps_its_allowed_range_when_the_limits_have_two_decimals()
    {
        var text = AlertWithZone("Vùng A", Salinity(12m, 15.25m, 30.75m));

        Assert.True(text.Contains("allowed 15.25-30.75 ppt"), text);
    }

    [Theory]
    [InlineData(15.5, 30.5)]
    [InlineData(15, 30)]
    [InlineData(5.25, 9.75)]
    public void A_range_of_fewer_digits_is_not_taken_for_an_account_number(decimal min, decimal max)
    {
        var text = AlertWithZone("Vùng A", Salinity(1m, min, max));

        Assert.DoesNotContain("***", text);
    }

    [Theory]
    [InlineData("GP-2026-0012")]
    [InlineData("QD-20260012")]
    [InlineData("12345678")]
    public void M3_a_license_reminder_names_its_license_when_the_number_has_8_digits(string number)
    {
        var text = LicenseReminder(number, ["KV01"]);

        Assert.True(text.Contains(number), text);
    }

    [Fact]
    public void M3_a_license_reminder_names_its_zone_when_the_code_has_8_digits()
    {
        var text = LicenseReminder("GP-01", ["KV-2026-0012"]);

        Assert.True(text.Contains("KV-2026-0012"), text);
    }

    [Fact]
    public void A_license_number_with_slashes_and_few_digits_stays_as_it_is()
    {
        Assert.Contains("123/GP-UBND", LicenseReminder("123/GP-UBND", ["KV01"]));
    }

    // ---- M4: the cut at 160 removes the facts ----

    // An ordinary name. BR-WATER-05: "each value outside, with its allowed range".
    [Fact]
    public void M4_a_two_value_alert_on_an_ordinary_zone_name_keeps_both_values_and_ranges()
    {
        var text = AlertWithZone("Vùng nuôi hàu Vịnh Bái Tử Long khu số 2", Ph(), Salinity());

        Assert.True(text.Contains("allowed 15-30 ppt"), text);
    }

    [Fact]
    public void M4_a_corrected_two_value_alert_on_an_ordinary_zone_name_still_names_the_second_value()
    {
        var text = Render(
            NotificationTypes.WaterAlert,
            new NotificationContent(
                NotificationTypes.WaterAlert,
                "Title",
                UserMessages.WaterAlert(true, "Vùng nuôi hàu Vịnh Bái Tử Long khu số 2", null, MeasuredAt, [Ph(), Salinity()])));

        Assert.True(text.Contains("Salinity 12"), text);
    }

    // Lengths.Name is 200: a long name pushes out the value itself.
    [Fact]
    public void M4_a_one_value_alert_keeps_the_value_whatever_the_length_of_the_zone_name()
    {
        var text = AlertWithZone(string.Join(' ', Enumerable.Repeat("Vùng nuôi hàu", 12)), Ph());

        Assert.True(text.Contains("pH 9.1"), text);
    }

    // MSG-FARM-01: "{document} expires on {expiry_date}, in {days} days".
    [Fact]
    public void M4_a_license_reminder_keeps_the_expiry_date_when_it_covers_many_zones()
    {
        var text = LicenseReminder("GP-01", Enumerable.Range(1, 12).Select(i => $"KV-HL-{i:00}").ToList());

        Assert.True(text.Contains("29/11/2026"), text);
    }

    // ---- GSM 7-bit, the 160 limit ----

    public static TheoryData<string> OperationalTypes() => new()
    {
        NotificationTypes.WaterAlert, NotificationTypes.WaterAlertWithdrawn, NotificationTypes.ZoneLicenseExpiry,
    };

    // The real GSM 03.38 default table, without its escape: the claim of Alphabet checked against the standard.
    [Fact]
    public void Every_character_of_the_alphabet_is_one_septet_of_the_gsm_default_table()
    {
        const string gsm = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

        Assert.All(NotificationSmsText.Alphabet, c => Assert.Contains(c, gsm));
    }

    [Theory]
    [MemberData(nameof(OperationalTypes))]
    public void Every_utf16_unit_in_a_message_comes_out_in_the_alphabet(string type)
    {
        for (var unit = 0; unit <= char.MaxValue; unit += 1)
        {
            var text = Render(type, new NotificationContent(type, "Title", new UserMessage("MSG-TEST", $"A{(char)unit}B")));

            Assert.True(text.All(NotificationSmsText.Alphabet.Contains), $"U+{unit:X4} gave \"{text}\".");
        }
    }

    [Theory]
    [InlineData("Đầm Hà Đông 🦪🦪 محار 牡蛎 Устрица 👨‍👩‍👧 Ñandú Straße", "Dam Ha Dong")]
    [InlineData("ĂÂÊÔƠƯĐăâêôơưđ", "AAEOOUDaaeooud")]
    public void Mixed_scripts_and_emoji_fall_into_the_alphabet_and_vietnamese_keeps_its_letters(string zone, string kept)
    {
        var text = AlertWithZone(zone, Ph());

        Assert.All(text, c => Assert.Contains(c, NotificationSmsText.Alphabet));
        Assert.Contains(kept, text);
    }

    [Fact]
    public void No_length_of_text_goes_over_160_and_a_shortened_text_says_so()
    {
        foreach (var separator in new[] { " ", "x", "1", "1 ", "." })
        {
            for (var length = 1; length <= 400; length++)
            {
                var body = string.Concat(Enumerable.Repeat(separator, length));
                var text = Render(NotificationTypes.WaterAlert, new NotificationContent(NotificationTypes.WaterAlert, "Title", new UserMessage("MSG-TEST", body)));

                Assert.True(text.Length <= NotificationSmsText.MaxLength, $"{length} of \"{separator}\" gave {text.Length}.");
            }
        }

        var cut = Render(NotificationTypes.WaterAlert, new NotificationContent(NotificationTypes.WaterAlert, "Title", new UserMessage("MSG-TEST", new string('x', 161))));
        Assert.EndsWith("...", cut);
        Assert.Equal(160, cut.Length);
    }

    // ---- Money ----

    // DocumentCodes builds PR-yyyyMMdd-nnn: 11 digits that read as they are.
    [Theory]
    [InlineData("PR-20261105-001")]
    [InlineData("PC-0007")]
    public void A_money_sms_keeps_a_document_code_that_holds_a_date(string code)
    {
        Assert.Equal($"Supplier payment transaction {code}: 12,500,000 VND.", RenderMoney(code, 12_500_000m));
    }

    [Theory]
    [MemberData(nameof(Amounts))]
    public void A_money_sms_formats_each_numeric_amount(object amount, string expected)
    {
        Assert.Equal($"Supplier payment transaction PC-1: {expected} VND.", RenderMoney("PC-1", amount));
    }

    public static TheoryData<object, string> Amounts() => new()
    {
        { 12_500_000m, "12,500,000" },
        { 0m, "0" },
        { -1_000m, "-1,000" },
        { 999m, "999" },
        { 1_000, "1,000" },
        { 1_234_567_890_123L, "1,234,567,890,123" },
        { 2_500_000d, "2,500,000" },
    };

    // The longest code the column holds with the amount of a large document: the cut cannot reach the amount.
    [Fact]
    public void The_longest_document_code_and_a_large_amount_fit_and_are_not_cut()
    {
        var code = new string('C', 50);

        Assert.Equal($"Supplier payment transaction {code}: 999,999,999,999 VND.", RenderMoney(code, 999_999_999_999m));
    }

    [Theory]
    [InlineData("PC 1🦪")]
    [InlineData("PC\r\n1")]
    public void A_hostile_document_code_cannot_bring_anything_outside_the_alphabet(string code)
    {
        var text = RenderMoney(code, 1_000m);

        Assert.All(text, c => Assert.Contains(c, NotificationSmsText.Alphabet));
        Assert.Contains("PC 1", text);
        Assert.EndsWith("1,000 VND.", text);
    }

    // Observations, no defect today: both depend on what a sibling module passes (payment.Amount is decimal, codes are generated).
    [Fact]
    public void Observation_an_amount_that_is_not_a_listed_number_type_is_dropped_silently()
    {
        Assert.Equal("Supplier payment transaction PC-1.", RenderMoney("PC-1", "12,500,000"));
        Assert.Equal("Supplier payment transaction PC-1.", RenderMoney("PC-1", 12_500f));
        Assert.Equal("Supplier payment transaction PC-1.", RenderMoney("PC-1", (short)500));
    }

    [Fact]
    public void Observation_the_document_code_of_a_money_sms_is_not_masked()
    {
        Assert.Contains(Account, RenderMoney(Account, 1_000m));
    }

    [Fact]
    public async Task An_amount_that_cannot_be_formatted_loses_the_sms_and_never_the_business_action()
    {
        var sms = new MandatorySmsQueueFakes();
        var owner = sms.Owner();
        var content = new NotificationContent(
            MoneyType.Code, "Title", new UserMessage("MSG-TEST", "Body") { Parameters = new Dictionary<string, object?> { ["amount"] = double.NaN } });

        await sms.Queue.QueueAsync(MoneyType, content, [owner.AccountId], CancellationToken.None);

        Assert.Empty(sms.Queued);
    }

    private static string LicenseReminder(string number, IReadOnlyList<string> zoneCodes) => Render(
        NotificationTypes.ZoneLicenseExpiry,
        new NotificationContent(
            NotificationTypes.ZoneLicenseExpiry,
            "Title",
            UserMessages.LicenseExpiringSoon("AQUACULTURE_LICENSE", "Marine aquaculture license", number, zoneCodes, new DateOnly(2026, 11, 29), 30)));
}
