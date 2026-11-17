using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.UnitTests.Features.Notifications;

/// <summary>
/// The rule of the SMS text (decision 2 of OMTS-216), tested on the rendering of every type of the catalogue, so a type
/// added later cannot break it silently.
/// </summary>
public class NotificationSmsTextTests
{
    // Not a real account number; generated from a pattern so that no account-like literal is committed.
    private static readonly string BankAccount = string.Concat(Enumerable.Range(1, 13).Select(i => i % 10));
    private static readonly string GroupedBankAccount = string.Join(' ', BankAccount.Chunk(4).Select(c => new string(c)));
    private static readonly DateTimeOffset MeasuredAt = new(2026, 10, 19, 23, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// The types whose SMS is the MSG text. A money type must never be added here: it uses <see cref="NotificationSmsRule.MoneyToOwner"/>.
    /// Adding an operational type means reading its MSG text for what it would put on a phone.
    /// </summary>
    private static readonly string[] TypesSendingTheMessageText =
    [
        NotificationTypes.WaterAlert, NotificationTypes.WaterAlertWithdrawn, NotificationTypes.ZoneLicenseExpiry,
        NotificationTypes.CooperativeCertificationExpiry, NotificationTypes.PurchaseRequestNew, NotificationTypes.CycleCloseReminder,
    ];

    private static readonly NotificationTypeDefinition MoneyType = new(
        "SUPPLIER_TRANSACTION", "Supplier payment", NotificationPushPolicy.AlwaysPush, [NotificationRole.CooperativeOwner],
        NotificationOpenAccess.OwnerOnly, NotificationSmsRule.MoneyToOwner("amount"));

    [Fact]
    public void Every_mandatory_type_names_its_sms_and_no_other_type_has_one()
    {
        Assert.All(NotificationTypes.All, t => Assert.Equal(t.IsMandatory, t.Sms is not null));
    }

    [Fact]
    public void Only_the_listed_types_send_their_message_text_so_a_money_type_cannot_join_them_unnoticed()
    {
        var sendingTheText = NotificationTypes.All.Where(t => t.Sms is { AmountParameter: null }).Select(t => t.Code);

        Assert.Equal(TypesSendingTheMessageText.Order(), sendingTheText.Order());
    }

    [Fact]
    public void Only_a_water_type_goes_to_zone_managers()
    {
        var types = NotificationTypes.All.Where(t => t.Sms?.To.Contains(NotificationRole.ZoneManager) == true).Select(t => t.Code);

        Assert.Equal([NotificationTypes.WaterAlert, NotificationTypes.WaterAlertWithdrawn], types);
    }

    // By construction: a money SMS is built from the code and the amount only. An operational text reads as its fields do.
    [Fact]
    public void A_money_type_puts_no_bank_account_number_on_a_phone_even_when_everything_else_holds_one()
    {
        var text = Render(MoneyType, Hostile(MoneyType.Code));

        Assert.Equal("Supplier payment DOC-01: 1,000 VND.", text);
        Assert.DoesNotContain(BankAccount, Digits(text));
        Assert.DoesNotContain(GroupedBankAccount, text);
    }

    [Fact]
    public void An_operational_text_reads_as_its_fields_do_whatever_their_digits()
    {
        var alert = Render(NotificationTypes.Find(NotificationTypes.WaterAlert)!, WaterAlert("KV-2026-0012", WaterValue(WaterValueOutside.Ph, 9.1m)));

        Assert.Contains("zone KV-2026-0012,", alert);
    }

    [Fact]
    public void A_money_sms_carries_the_document_code_and_the_amount_only()
    {
        var content = new NotificationContent(
            MoneyType.Code,
            "A title with a supplier name",
            new UserMessage("MSG-PURCHASE-04", $"Nguyễn Văn A recorded payment PC-0007 to Công ty Biển Xanh, account {BankAccount}. The cooperative owes 3,000,000 VND.")
            {
                Parameters = new Dictionary<string, object?> { ["amount"] = 12_500_000m, ["bank_account"] = BankAccount, ["supplier_name"] = "Công ty Biển Xanh" },
            },
            new RelatedRecord("SupplierPayment", Guid.NewGuid(), "PC-0007"));

        Assert.Equal("Supplier payment PC-0007: 12,500,000 VND.", Render(MoneyType, content));
    }

    // A payment that lost its amount must not fall back to the MSG text.
    [Fact]
    public void A_money_sms_without_an_amount_or_a_code_still_holds_nothing_else()
    {
        var content = new NotificationContent(MoneyType.Code, "Title", new UserMessage("MSG-PURCHASE-04", $"Account {BankAccount}"));

        Assert.Equal("Supplier payment.", Render(MoneyType, content));
    }

    [Fact]
    public void A_water_alert_reads_as_its_message_in_the_sms_alphabet()
    {
        var text = Render(NotificationTypes.Find(NotificationTypes.WaterAlert)!, WaterAlert("Vùng Cái Rồng", WaterValue(WaterValueOutside.Salinity, 12m)));

        Assert.Equal(
            "Water alert: zone Vung Cai Rong, raft -, measured on 06:30 20/10/2026. Salinity 12 ppt is too low (allowed 15-30 ppt). Please check the water.",
            text);
    }

    [Fact]
    public void A_license_reminder_reads_as_its_message()
    {
        var message = UserMessages.LicenseExpiringSoon("AQUACULTURE_LICENSE", "Marine aquaculture license", "GP-01", "KV01", new DateOnly(2026, 11, 29), 30);
        var text = Render(NotificationTypes.Find(NotificationTypes.ZoneLicenseExpiry)!, new NotificationContent(NotificationTypes.ZoneLicenseExpiry, "Title", message));

        Assert.Equal("Marine aquaculture license GP-01 (KV01) expires on 29/11/2026, in 30 days. Please renew it and record the new one.", text);
    }

    [Fact]
    public void A_new_purchase_request_goes_to_the_owner_and_reads_as_its_message()
    {
        var text = RenderRequest("Chị Lan", 301, "Hàu sữa", "Lấy sớm");

        Assert.Equal([NotificationRole.CooperativeOwner], NotificationTypes.Find(NotificationTypes.PurchaseRequestNew)!.Sms!.To);
        Assert.Equal("Chi Lan asks to buy 301 cages of Hau sua, pickup on 07/11/2026. Lay som Please ask the Zone Manager to check the oysters, then respond.", text);
    }

    [Theory]
    [InlineData(40, 40, 400)]
    [InlineData(40, 200, 0)]
    [InlineData(200, 200, 2000)]
    public void A_long_new_purchase_request_keeps_the_cages_and_the_date_and_fits_one_sms(int customerLength, int productLength, int noteLength)
    {
        var text = RenderRequest(new string('Ư', customerLength), 2147483647, new string('à', productLength), noteLength == 0 ? null : new string('Đ', noteLength));

        Assert.True(text.Length <= NotificationSmsText.MaxLength, text);
        Assert.All(text, c => Assert.Contains(c, NotificationSmsText.Alphabet));
        Assert.Contains("2147483647 cages", text);
        Assert.Contains("07/11/2026", text);
    }

    // The note gives way first: an ordinary customer and product stay whole while the closing sentence is kept.
    [Fact]
    public void A_long_note_gives_way_before_the_customer_the_product_and_the_closing_sentence()
    {
        var text = RenderRequest("Chị Lan", 301, "Hàu sữa", string.Join(' ', Enumerable.Repeat("lấy sớm ở cảng", 20)));

        Assert.True(text.Length <= NotificationSmsText.MaxLength, text);
        Assert.Contains("Chi Lan asks to buy 301 cages of Hau sua, pickup on 07/11/2026.", text);
        Assert.Contains("lay som", text);
        Assert.Contains("...", text);
        Assert.EndsWith("Please ask the Zone Manager to check the oysters, then respond.", text);
    }

    // A request counts whole cages, so even the largest count leaves room for the closing sentence once the three fields are at their minimums.
    [Fact]
    public void The_closing_sentence_stays_when_the_free_text_shrinks_to_its_minimum()
    {
        var text = RenderRequest(new string('C', 200), 2147483647, new string('P', 200), new string('N', 400));

        Assert.True(text.Length <= NotificationSmsText.MaxLength, text);
        Assert.EndsWith("Please ask the Zone Manager to check the oysters, then respond.", text);
        Assert.Contains("C...", text);
        Assert.Contains("2147483647 cages of P", text);
    }

    [Fact]
    public void A_new_purchase_request_without_a_note_shortens_the_customer_before_the_product()
    {
        var text = RenderRequest(new string('C', 200), 300, new string('P', 20), null);

        Assert.True(text.Length <= NotificationSmsText.MaxLength, text);
        Assert.Contains("C... asks to buy 300 cages of " + new string('P', 20) + ", pickup on 07/11/2026.", text);
    }

    [Theory]
    [InlineData("Đầm Hà Đông", "Dam Ha Dong")]
    [InlineData("VỊNH BÁI TỬ LONG", "VINH BAI TU LONG")]
    // The same letters written decomposed, base letter and marks apart.
    [InlineData("Vùng Cái", "Vung Cai")]
    [InlineData("Zone🦪 [A]\tB", "Zone A B")]
    public void Vietnamese_and_other_letters_become_the_sms_alphabet(string zone, string expected)
    {
        var text = Render(NotificationTypes.Find(NotificationTypes.WaterAlert)!, WaterAlert(zone, WaterValue(WaterValueOutside.Ph, 9.1m)));

        Assert.Contains($"zone {expected},", text);
    }

    [Fact]
    public void The_longest_alert_fits_one_sms_in_the_alphabet()
    {
        var text = Render(
            NotificationTypes.Find(NotificationTypes.WaterAlert)!,
            WaterAlert(new string('Ư', 200), WaterValue(WaterValueOutside.Ph, 9.1m), WaterValue(WaterValueOutside.Salinity, 12m), corrected: true));

        Assert.Equal(NotificationSmsText.MaxLength, text.Length);
        Assert.All(text, c => Assert.Contains(c, NotificationSmsText.Alphabet));
    }

    [Fact]
    public void An_alert_with_two_values_keeps_the_zone_and_both_values_when_it_is_shortened()
    {
        var text = Render(
            NotificationTypes.Find(NotificationTypes.WaterAlert)!,
            WaterAlert("Vùng Cái Rồng", WaterValue(WaterValueOutside.Ph, 9.1m), WaterValue(WaterValueOutside.Salinity, 12m), corrected: true));

        Assert.True(text.Length <= NotificationSmsText.MaxLength, text);
        Assert.Contains("Vung Cai Rong", text);
        Assert.Contains("pH 9.1", text);
        Assert.Contains("Salinity 12", text);
    }

    [Fact]
    public void Dates_times_and_short_codes_are_not_taken_for_an_account_number()
    {
        var text = Render(NotificationTypes.Find(NotificationTypes.WaterAlert)!, WaterAlert("KV 01-2", WaterValue(WaterValueOutside.Ph, 9.1m)));

        Assert.Contains("06:30 20/10/2026", text);
        Assert.Contains("KV 01-2", text);
        Assert.DoesNotContain("***", text);
    }

    private static string Render(NotificationTypeDefinition definition, NotificationContent content) =>
        NotificationSmsText.Render(definition, definition.Sms!, content);

    private static string RenderRequest(string customer, int cages, string product, string? note) => Render(
        NotificationTypes.Find(NotificationTypes.PurchaseRequestNew)!,
        new NotificationContent(NotificationTypes.PurchaseRequestNew, "New purchase request", UserMessages.PurchaseRequestNew(customer, cages, product, new DateOnly(2026, 11, 7), note)));

    private static string Digits(string text) => new(text.Where(char.IsAsciiDigit).ToArray());

    /// <summary>Everything a careless module could put in a notification holds the number, in one piece and grouped.</summary>
    private static NotificationContent Hostile(string type) => new(
        type,
        $"Title {BankAccount}",
        new UserMessage("MSG-TEST", $"Transfer to account {BankAccount} or {GroupedBankAccount} of Ngân hàng Biển Đông.")
        {
            Parameters = new Dictionary<string, object?> { ["amount"] = 1_000m, ["bank_account"] = BankAccount },
        },
        new RelatedRecord("Any", Guid.NewGuid(), "DOC-01"));

    private static NotificationContent WaterAlert(string zone, WaterValueOutside first, WaterValueOutside? second = null, bool corrected = false) => new(
        NotificationTypes.WaterAlert,
        "Water alert",
        UserMessages.WaterAlert(corrected, zone, "-", MeasuredAt, second is null ? [first] : [first, second]));

    private static WaterValueOutside WaterValue(string indicator, decimal value) => indicator == WaterValueOutside.Ph
        ? new WaterValueOutside(indicator, value, null, WaterValueDirection.High, 6.5m, 8.5m)
        : new WaterValueOutside(indicator, value, "‰", WaterValueDirection.Low, 15m, 30m);
}
