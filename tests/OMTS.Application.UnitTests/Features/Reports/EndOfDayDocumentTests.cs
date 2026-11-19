using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Reports;
using OMTS.Application.Features.Reports.Documents;
using OMTS.Application.Features.Reports.EndOfDay;

namespace OMTS.Application.UnitTests.Features.Reports;

/// <summary>The printable layout of the end-of-day report: the title and filter lines of each concern, the paper direction and the rows.</summary>
public class EndOfDayDocumentTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 9, 5, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Made = new(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(7));

    private static EndOfDayChoices Choices(IReadOnlyList<ReportChoice>? customers = null, IReadOnlyList<ReportChoice>? createdBy = null, IReadOnlyList<string>? methods = null) => new(
        [], customers ?? [], [], [], [], [], createdBy ?? [], [], methods ?? [], [], [], [], [], []);

    private static EndOfDayReport Report(EndOfDayConcern concern, EndOfDayPeriod period, EndOfDayChoices? choices = null, EndOfDaySales? sales = null, EndOfDayGoods? goods = null) => new(
        Made, concern.ToString().ToLowerInvariant(), period.FromText, period.ToText, choices ?? Choices(), sales, null, goods, null, null, null);

    private static EndOfDayPeriod Period(string from, string to) => EndOfDayPeriod.Resolve(null, from, to, new DateOnly(2026, 11, 19)).Value!;

    private static EndOfDaySales Sales()
    {
        var invoice = new EndOfDayInvoiceFact(Guid.NewGuid(), "HD000001", At, null, null, "Zone A", 10m, 1_000_000m, 0m, 0m, 1_000_000m, 1_000_000m, 1_000_000m, 0m);
        var order = new EndOfDayOrderFact(Guid.NewGuid(), "DH000001", At, null, null, null, 20m, 2_000_000m, 0m, 500_000m);
        var back = new EndOfDayReturnFact(Guid.NewGuid(), "TL000001", At, invoice.Id, "HD000001", null, null, "Zone A", 10m, 1_000_000m);
        return EndOfDayCalculator.SalesOf(new EndOfDaySalesFacts([order], [invoice], [back]));
    }

    [Theory]
    [InlineData(EndOfDayConcern.Sales, "Báo cáo cuối ngày về bán hàng")]
    [InlineData(EndOfDayConcern.Money, "Báo cáo cuối ngày về thu chi")]
    [InlineData(EndOfDayConcern.Goods, "Báo cáo cuối ngày về hàng hóa")]
    [InlineData(EndOfDayConcern.Summary, "Báo cáo cuối ngày về tổng hợp")]
    public void The_title_follows_the_concern(EndOfDayConcern concern, string title) => Assert.Equal(title, EndOfDayDocumentBuilder.Title(concern));

    [Fact]
    public void The_paper_is_portrait_unless_landscape_is_asked_for()
    {
        var period = Period("2026-11-19T00:00", "2026-11-19T23:59");
        var report = Report(EndOfDayConcern.Sales, period, sales: Sales());

        Assert.Equal(ReportOrientation.Portrait, EndOfDayDocumentBuilder.Build(report, period, "HTX", Made, ReportOrientation.Portrait, false).Orientation);
        Assert.Equal(ReportOrientation.Landscape, EndOfDayDocumentBuilder.Build(report, period, "HTX", Made, ReportOrientation.Landscape, false).Orientation);
        Assert.Equal(ReportOrientation.Portrait, new ReportDocument("t", Made.DateTime, [], [], string.Empty).Orientation);
    }

    [Theory]
    [InlineData(null, ReportOrientation.Portrait, true)]
    [InlineData("", ReportOrientation.Portrait, true)]
    [InlineData("Portrait", ReportOrientation.Portrait, true)]
    [InlineData("LANDSCAPE", ReportOrientation.Landscape, true)]
    [InlineData("sideways", ReportOrientation.Portrait, false)]
    public void The_orientation_is_read_in_any_letter_case_and_anything_else_is_not_ok(string? value, ReportOrientation expected, bool ok) =>
        Assert.Equal((expected, ok), EndOfDayExportService.ParseOrientation(value));

    [Fact]
    public void The_filter_lines_name_the_period_and_the_people_chosen()
    {
        var period = Period("2026-11-18T08:00", "2026-11-19T17:30");
        var choices = Choices([new ReportChoice(Guid.NewGuid(), "Nguyễn Văn A")], [new ReportChoice(Guid.NewGuid(), "Lê Thị B")], ["Cash", "EWallet"]);

        var document = EndOfDayDocumentBuilder.Build(Report(EndOfDayConcern.Sales, period, choices, Sales()), period, "HTX", Made, ReportOrientation.Portrait, false);

        Assert.Equal("Thời gian: 18/11/2026 08:00 - 19/11/2026 17:30", document.FilterLines[0]);
        Assert.Equal("Vùng nuôi: Tất cả vùng", document.FilterLines[1]);
        Assert.Contains("Khách hàng: Nguyễn Văn A", document.FilterLines);
        Assert.Contains("Người tạo: Lê Thị B", document.FilterLines);
        Assert.Contains("Phương thức thanh toán: Tiền mặt, Ví điện tử", document.FilterLines);
        Assert.DoesNotContain(document.FilterLines, l => l.Contains("tài khoản đã chọn"));
    }

    [Fact]
    public void The_sales_table_has_a_group_per_kind_with_a_row_per_document()
    {
        var period = Period("2026-11-19T00:00", "2026-11-19T23:59");

        var section = EndOfDayDocumentBuilder.Build(Report(EndOfDayConcern.Sales, period, sales: Sales()), period, "HTX", Made, ReportOrientation.Portrait, false).Sections.Single();

        Assert.Equal(
            ["Mã giao dịch", "Thời gian", "SL (kg)", "Doanh thu", "Giảm giá", "Thu khác", "Thực thu", "Còn nợ"], section.Columns.Select(c => c.Title).ToArray());
        var labels = section.Rows.Where(r => r.Kind == ReportRowKind.Group).Select(r => (string)r.Cells[0]!).ToArray();
        Assert.Equal(["Đặt hàng: 1", "Hóa đơn: 1", "Trả hàng: 1", "Tổng cộng", "Doanh thu thuần"], labels);
        Assert.Equal(["DH000001", "HD000001", "TL000001"], section.Rows.Where(r => r.Kind == ReportRowKind.Detail).Select(r => (string)r.Cells[0]!).ToArray());
    }

    [Fact]
    public void The_time_column_shows_the_day_only_when_the_period_has_more_than_one()
    {
        var oneDay = Period("2026-11-19T00:00", "2026-11-19T23:59");
        var manyDays = Period("2026-11-18T00:00", "2026-11-19T23:59");

        string TimeOfFirstDetail(EndOfDayPeriod period) =>
            (string)EndOfDayDocumentBuilder.Build(Report(EndOfDayConcern.Sales, period, sales: Sales()), period, "HTX", Made, ReportOrientation.Portrait, false)
                .Sections.Single().Rows.First(r => r.Kind == ReportRowKind.Detail).Cells[1]!;

        Assert.Equal("09:05", TimeOfFirstDetail(oneDay));
        Assert.Equal("19/11 09:05", TimeOfFirstDetail(manyDays));
    }

    private static EndOfDayGoods Goods()
    {
        var invoice = Guid.NewGuid();
        var line = new EndOfDayLineFact(Guid.NewGuid(), invoice, "HD1", At, "Khách A", 1, Guid.NewGuid(), "H01", "Hàu", null, 10m, 300_000m, 30_000m);
        return EndOfDayCalculator.GoodsOf(new EndOfDayGoodsFacts([line], [], []), [], [], []);
    }

    [Fact]
    public void The_goods_table_has_the_columns_and_the_note_about_the_shared_discount()
    {
        var period = Period("2026-11-19T00:00", "2026-11-19T23:59");

        var document = EndOfDayDocumentBuilder.Build(Report(EndOfDayConcern.Goods, period, goods: Goods()), period, "HTX", Made, ReportOrientation.Portrait, false);

        var section = document.Sections.Single();
        Assert.Equal(
            ["Mã hàng", "Tên hàng", "SL bán", "Doanh thu", "SL trả", "Giá trị trả", "Doanh thu thuần"], section.Columns.Select(c => c.Title).ToArray());
        Assert.Equal("(Đã phân bổ giảm giá hóa đơn)", section.Note);
        Assert.Equal([ReportRowKind.Group, ReportRowKind.Detail], section.Rows.Select(r => r.Kind).ToArray());
        Assert.Equal(270_000m, section.Rows[1].Cells[3]);
    }

    [Fact]
    public void Expanded_goods_print_the_documents_under_each_product()
    {
        var period = Period("2026-11-19T00:00", "2026-11-19T23:59");

        var section = EndOfDayDocumentBuilder.Build(Report(EndOfDayConcern.Goods, period, goods: Goods()), period, "HTX", Made, ReportOrientation.Landscape, true).Sections.Single();

        Assert.Equal([ReportRowKind.Group, ReportRowKind.SubGroup, ReportRowKind.Detail], section.Rows.Select(r => r.Kind).ToArray());
        Assert.Equal("HD1", section.Rows[2].Cells[0]);
        Assert.Equal(10m, section.Rows[2].Cells[2]);
    }
}
