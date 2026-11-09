using OMTS.Application.Features.Cooperatives;
using OMTS.Application.Features.Traceability.Origin;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Traceability;

/// <summary>ORIGIN-01: which lots the page lists (BR-ORIGIN-05), what a Published and a Withdrawn lot show (BR-ORIGIN-03, ORIGIN-01.1), the two languages (BR-ORIGIN-02).</summary>
public sealed class ProvenancePageBuilderTests
{
    private static readonly DateOnly HarvestDate = new(2026, 11, 8);
    private static readonly Guid PublishedId = Guid.NewGuid();
    private static readonly Guid WithdrawnId = Guid.NewGuid();
    private static readonly Guid NotPublishedId = Guid.NewGuid();

    private static PublicCooperativePreview Cooperative(string? introductionEn = "We farm oysters.") => new(
        "DEMO Oyster Cooperative", "DEMO HTX", 2015, "DEMO address", "DEMO-PHONE", null, null, null, null, null, "Chúng tôi nuôi hàu.", introductionEn ?? "Chúng tôi nuôi hàu.", []);

    private static ProvenanceLotRow LotRow(Guid id, string code, DateTimeOffset? publishedAt, DateTimeOffset? withdrawnAt, decimal? raftLat = 21.05m, decimal? zoneLat = 21m) => new(
        id, code, HarvestDate, publishedAt, withdrawnAt, "KV01-B01", raftLat, raftLat is null ? null : 107.4m, "Zone one", zoneLat, zoneLat is null ? null : 107.3m,
        ["harvest-lots/2026/11/a.jpg"], ["rafts/2026/11/b.jpg"]);

    private static readonly DateTimeOffset Published = new(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

    // Withdrawn at 20:00 UTC on 07/11, which is 03:00 on 08/11 in Vietnam: the page shows the business date.
    private static readonly DateTimeOffset Withdrawn = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);

    private static ProvenanceInvoiceRow Invoice(bool cancelled = false) => new("HD000007", new DateOnly(2026, 11, 8), cancelled,
    [
        new(1, WithdrawnId, "Oysters grade A"),
        new(2, PublishedId, "Oysters grade A"),
        new(3, NotPublishedId, "Oysters grade B"),
        new(4, PublishedId, "Oysters grade B"),
        new(5, PublishedId, "Oysters grade A"),
    ]);

    private static readonly Dictionary<string, string> Urls = new() { ["harvest-lots/2026/11/a.jpg"] = "https://files/a", ["rafts/2026/11/b.jpg"] = "https://files/b" };

    private static ProvenancePage Page(string language, IReadOnlyList<ProvenanceLotRow>? lots = null, IReadOnlyList<ProvenanceSeedingRow>? seedings = null) =>
        ProvenancePageBuilder.Build(
            Invoice(),
            lots ?? [LotRow(PublishedId, "KV01-B01-20261108", Published, null), LotRow(WithdrawnId, "KV01-B01-20261101", Published.AddDays(-7), Withdrawn), LotRow(NotPublishedId, "KV01-B01-20261108-2", null, null)],
            seedings ?? [new(PublishedId, new DateOnly(2026, 6, 3), "Seed oyster"), new(PublishedId, new DateOnly(2026, 6, 1), "Seed oyster"), new(PublishedId, new DateOnly(2026, 6, 1), "Seed oyster")],
            Urls,
            Cooperative(),
            language);

    [Fact]
    public void The_page_lists_the_published_and_withdrawn_lots_in_line_order_with_their_products_and_hides_the_others()
    {
        var page = Page(ProvenanceLanguages.English);

        Assert.Equal((ProvenancePageStatus.Ok, null, "HD000007"), (page.Status, page.MessageCode, page.Invoice!.Code));
        Assert.Equal(["KV01-B01-20261101", "KV01-B01-20261108"], page.Lots.Select(l => l.LotCode));
        Assert.Equal(["Oysters grade A"], page.Lots[0].Products);
        Assert.Equal(["Oysters grade A", "Oysters grade B"], page.Lots[1].Products);
    }

    [Fact]
    public void A_published_lot_shows_its_origin_information_with_the_current_data()
    {
        var lot = Page(ProvenanceLanguages.English).Lots.Single(l => l.OriginState == HarvestLotOriginState.Published);

        Assert.Null(lot.Message);
        var origin = lot.Origin!;
        Assert.Equal((HarvestDate, "KV01-B01", "Zone one"), (origin.HarvestDate, origin.RaftCode, origin.ZoneName));
        Assert.Equal(new ProvenanceMapPoint(21.05m, 107.4m, ProvenanceMapSource.Raft), origin.Map);
        Assert.Equal([new ProvenanceSeeding(new DateOnly(2026, 6, 1), "Seed oyster"), new ProvenanceSeeding(new DateOnly(2026, 6, 3), "Seed oyster")], origin.Seedings);
        Assert.Equal(160, origin.DaysOfFarming);
        Assert.Equal(["https://files/a"], origin.HarvestPhotoUrls);
        Assert.Equal(["https://files/b"], origin.RaftPhotoUrls);
    }

    [Theory]
    [InlineData(ProvenanceLanguages.English, "DEMO HTX withdrew the origin information of lot KV01-B01-20261101 on 08/11/2026. For details, please call DEMO-PHONE.")]
    [InlineData(ProvenanceLanguages.Vietnamese, "DEMO HTX đã thu hồi thông tin nguồn gốc của lô KV01-B01-20261101 vào ngày 08/11/2026. Để biết thêm chi tiết, vui lòng gọi DEMO-PHONE.")]
    public void A_withdrawn_lot_shows_the_warning_with_the_business_date_and_no_origin(string language, string text)
    {
        var lot = Page(language).Lots.Single(l => l.OriginState == HarvestLotOriginState.Withdrawn);

        Assert.Equal(("MSG-ORIGIN-01", text, new DateOnly(2026, 11, 8)), (lot.MessageCode, lot.Message, lot.WithdrawalDate));
        Assert.Null(lot.Origin);
        Assert.Equal(["cooperative_short_name", "lot_code", "withdrawal_date", "cooperative_phone"], lot.MessageParams!.Keys);
    }

    [Theory]
    [InlineData(ProvenanceLanguages.English, "No origin information is shown for this invoice. For details, please call DEMO-PHONE.")]
    [InlineData(ProvenanceLanguages.Vietnamese, "Hóa đơn này không có thông tin nguồn gốc để hiển thị. Để biết thêm chi tiết, vui lòng gọi DEMO-PHONE.")]
    public void An_invoice_with_no_published_or_withdrawn_lot_says_so_and_lists_none(string language, string text)
    {
        var page = Page(language, lots: [LotRow(NotPublishedId, "KV01-B01-20261108-2", null, null)], seedings: []);

        Assert.Equal((ProvenancePageStatus.NoLots, "MSG-ORIGIN-02", text, "HD000007"), (page.Status, page.MessageCode, page.Message, page.Invoice?.Code));
        Assert.Empty(page.Lots);
    }

    [Theory]
    [InlineData(ProvenanceLanguages.English, "This invoice has been cancelled, so no origin information is shown. For details, please call DEMO-PHONE.")]
    [InlineData(ProvenanceLanguages.Vietnamese, "Hóa đơn này đã bị hủy nên không hiển thị thông tin nguồn gốc. Để biết thêm chi tiết, vui lòng gọi DEMO-PHONE.")]
    public void A_cancelled_invoice_shows_the_cooperative_block_only(string language, string text)
    {
        var page = ProvenancePageBuilder.Cancelled(Cooperative(), language);

        Assert.Equal((ProvenancePageStatus.Cancelled, "MSG-ORIGIN-04", text), (page.Status, page.MessageCode, page.Message));
        Assert.Null(page.Invoice);
        Assert.Empty(page.Lots);
        Assert.Equal("DEMO Oyster Cooperative", page.Cooperative.FullName);
    }

    // BR-ORIGIN-02: the introduction follows the language; the English page shows the Vietnamese one when the English one is empty.
    [Fact]
    public void The_introduction_follows_the_language_and_falls_back_to_vietnamese()
    {
        var invoice = Invoice();
        ProvenancePage Of(string language, string? en) => ProvenancePageBuilder.Build(invoice, [], [], Urls, Cooperative(en), language);

        Assert.Equal("Chúng tôi nuôi hàu.", Of(ProvenanceLanguages.Vietnamese, "We farm oysters.").Cooperative.Introduction);
        Assert.Equal("We farm oysters.", Of(ProvenanceLanguages.English, "We farm oysters.").Cooperative.Introduction);
        Assert.Equal("Chúng tôi nuôi hàu.", Of(ProvenanceLanguages.English, "  ").Cooperative.Introduction);
        Assert.Equal("Chúng tôi nuôi hàu.", Of(ProvenanceLanguages.English, null).Cooperative.Introduction);
    }

    [Theory]
    [InlineData(null, ProvenanceLanguages.Vietnamese)]
    [InlineData("vi", ProvenanceLanguages.Vietnamese)]
    [InlineData("EN", ProvenanceLanguages.English)]
    [InlineData(" en ", ProvenanceLanguages.English)]
    [InlineData("fr", ProvenanceLanguages.Vietnamese)]
    public void The_page_opens_in_vietnamese_unless_english_is_asked_for(string? lang, string expected) => Assert.Equal(expected, ProvenanceLanguages.Of(lang));

    [Fact]
    public void The_map_is_the_raft_then_the_zone_then_none()
    {
        Assert.Equal(ProvenanceMapSource.Raft, ProvenancePageBuilder.Map(LotRow(PublishedId, "L", Published, null))!.Source);
        Assert.Equal(new ProvenanceMapPoint(21m, 107.3m, ProvenanceMapSource.Zone), ProvenancePageBuilder.Map(LotRow(PublishedId, "L", Published, null, raftLat: null)));
        Assert.Null(ProvenancePageBuilder.Map(LotRow(PublishedId, "L", Published, null, raftLat: null, zoneLat: null)));
    }

    [Fact]
    public void Days_of_farming_run_from_the_earliest_seeding_to_the_harvest_and_are_unknown_without_a_session()
    {
        Assert.Equal(7, ProvenancePageBuilder.DaysOfFarming(HarvestDate, [HarvestDate.AddDays(-3), HarvestDate.AddDays(-7)]));
        Assert.Null(ProvenancePageBuilder.DaysOfFarming(HarvestDate, []));
    }

    // A lot published again after a withdrawal (BR-TRACE-02) is Published: it shows its origin information at once (BR-ORIGIN-05).
    [Fact]
    public void A_lot_published_again_after_its_withdrawal_shows_its_origin_again()
    {
        var page = Page(ProvenanceLanguages.English, lots: [LotRow(WithdrawnId, "KV01-B01-20261101", Withdrawn.AddHours(1), Withdrawn)], seedings: []);

        var lot = Assert.Single(page.Lots);
        Assert.Equal((HarvestLotOriginState.Published, null), (lot.OriginState, lot.WithdrawalDate));
        Assert.NotNull(lot.Origin);
    }
}
