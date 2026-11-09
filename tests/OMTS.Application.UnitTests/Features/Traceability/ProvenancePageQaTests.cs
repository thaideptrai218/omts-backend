using OMTS.Application.Features.Cooperatives;
using OMTS.Application.Features.Traceability.Origin;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Traceability;

/// <summary>QA of the ORIGIN-01 page builder (OMTS-222): the edges the developer's tests leave out.</summary>
public sealed class ProvenancePageQaTests
{
    private static readonly DateOnly HarvestDate = new(2026, 11, 8);
    private static readonly Guid LotId = Guid.NewGuid();

    private static PublicCooperativePreview Cooperative(string? shortName = "DEMO HTX", string? phone = "DEMO-PHONE") => new(
        "DEMO Oyster Cooperative", shortName, null, null, phone, null, null, null, null, null, "Giới thiệu.", "Giới thiệu.", []);

    private static ProvenanceLotRow Lot(DateTimeOffset? publishedAt, DateTimeOffset? withdrawnAt, Guid? id = null) => new(
        id ?? LotId, "KV01-B01-20261108", HarvestDate, publishedAt, withdrawnAt, "B01", null, null, "Zone one", null, null, [], []);

    private static ProvenanceInvoiceRow Invoice(params Guid[] lots) =>
        new("HD000009", HarvestDate, false, lots.Select((id, i) => new ProvenanceInvoiceLineRow(i + 1, id, "Oysters")).ToList());

    private static ProvenancePage Build(ProvenanceLotRow lot, IReadOnlyList<ProvenanceSeedingRow>? seedings = null, PublicCooperativePreview? cooperative = null, string language = "en") =>
        ProvenancePageBuilder.Build(Invoice(lot.Id), [lot], seedings ?? [], new Dictionary<string, string>(), cooperative ?? Cooperative(), language);

    // BR-ORIGIN-03 / BR-TRACE-03: a lane re-hung on the same day with seed of another item shows both items; the same session read twice (two lanes) shows once.
    [Fact]
    public void Qa_seedings_of_two_items_on_one_day_both_show_once_each_oldest_first()
    {
        var published = new DateTimeOffset(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);
        var day = new DateOnly(2026, 10, 1);
        var page = Build(Lot(published, null), [new(LotId, day, "Seed B"), new(LotId, day, "Seed A"), new(LotId, day, "Seed B"), new(LotId, day.AddDays(-3), "Seed B")]);

        var origin = Assert.Single(page.Lots).Origin!;
        Assert.Equal([new ProvenanceSeeding(day.AddDays(-3), "Seed B"), new ProvenanceSeeding(day, "Seed A"), new ProvenanceSeeding(day, "Seed B")], origin.Seedings);
        Assert.Equal(HarvestDate.DayNumber - day.AddDays(-3).DayNumber, origin.DaysOfFarming);
    }

    // BR-TRACE-02: withdrawn, published again, withdrawn again: Withdrawn, with the date of the latest withdrawal.
    [Fact]
    public void Qa_a_lot_withdrawn_again_after_a_republish_shows_the_latest_withdrawal_date()
    {
        var republished = new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);
        var withdrawnAgain = new DateTimeOffset(2026, 11, 12, 2, 0, 0, TimeSpan.Zero);

        var lot = Assert.Single(Build(Lot(republished, withdrawnAgain)).Lots);

        Assert.Equal((HarvestLotOriginState.Withdrawn, new DateOnly(2026, 11, 12)), (lot.OriginState, lot.WithdrawalDate));
        Assert.Null(lot.Origin);
    }

    // The business day starts at 00:00 in Vietnam (17:00 UTC): a withdrawal at that instant is dated the next day, one second before is dated the same day.
    [Theory]
    [InlineData(17, 0, 0, 12)]
    [InlineData(16, 59, 59, 11)]
    public void Qa_the_withdrawal_date_is_the_vietnam_business_date_at_the_midnight_edge(int hour, int minute, int second, int expectedDay)
    {
        var withdrawn = new DateTimeOffset(2026, 11, 11, hour, minute, second, TimeSpan.Zero);

        var lot = Assert.Single(Build(Lot(withdrawn.AddDays(-5), withdrawn)).Lots);

        Assert.Equal(new DateOnly(2026, 11, expectedDay), lot.WithdrawalDate);
        Assert.Contains($"on {expectedDay}/11/2026.", lot.Message);
    }

    // MSG-ORIGIN-01 on a profile without short name or phone: the full name stands in for the short name and "–" for the phone, in both languages; the phone parameter is null.
    [Theory]
    [InlineData("en", "DEMO Oyster Cooperative withdrew the origin information of lot KV01-B01-20261108 on 08/11/2026. For details, please call –.")]
    [InlineData("vi", "DEMO Oyster Cooperative đã thu hồi thông tin nguồn gốc của lô KV01-B01-20261108 vào ngày 08/11/2026. Để biết thêm chi tiết, vui lòng gọi –.")]
    public void Qa_a_withdrawn_lot_without_short_name_or_phone_falls_back_to_the_full_name_and_a_dash(string language, string text)
    {
        var withdrawn = new DateTimeOffset(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

        var lot = Assert.Single(Build(Lot(withdrawn.AddDays(-1), withdrawn), cooperative: Cooperative(shortName: null, phone: null), language: language).Lots);

        Assert.Equal(text, lot.Message);
        Assert.Null(lot.MessageParams!["cooperative_phone"]);
        Assert.Equal("DEMO Oyster Cooperative", lot.MessageParams["cooperative_short_name"]);
    }

    // A line whose lot is not among the rows read is not listed (never an exception); with nothing else listed the page says MSG-ORIGIN-02.
    [Fact]
    public void Qa_a_line_whose_lot_row_is_missing_is_left_out()
    {
        var page = ProvenancePageBuilder.Build(Invoice(Guid.NewGuid()), [], [], new Dictionary<string, string>(), Cooperative(), "vi");

        Assert.Equal((ProvenancePageStatus.NoLots, "MSG-ORIGIN-02"), (page.Status, page.MessageCode));
        Assert.Empty(page.Lots);
    }

    // BR-ORIGIN-02: the page opens in Vietnamese; only "en" (any case, spaces trimmed) is English — a browser tag such as "en-US" or an empty value stays Vietnamese.
    [Theory]
    [InlineData("", ProvenanceLanguages.Vietnamese)]
    [InlineData("en-US", ProvenanceLanguages.Vietnamese)]
    [InlineData("En", ProvenanceLanguages.English)]
    [InlineData("\ten\t", ProvenanceLanguages.English)]
    [InlineData("VI", ProvenanceLanguages.Vietnamese)]
    public void Qa_only_en_opens_the_page_in_english(string lang, string expected) => Assert.Equal(expected, ProvenanceLanguages.Of(lang));
}
