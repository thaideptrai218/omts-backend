using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Features.Reports.Documents;

namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>
/// Lays the end-of-day report out as a printable <see cref="ReportDocument"/> in the KiotViet style (PDF and Excel share it): the sales with a group per kind and one row per document, then the
/// money, the goods and the lots weighed, each as its own table so every column keeps one meaning. Changes nothing.
/// </summary>
public static class EndOfDayDocumentBuilder
{
    public const string Title = "Báo cáo cuối ngày về bán hàng";

    public static ReportDocument Build(EndOfDayReport report, string cooperativeName, DateTimeOffset createdAt)
    {
        var sections = new List<ReportSection> { SalesSection(report.Sales) };
        if (report.Money.Entries.Count > 0)
        {
            sections.Add(MoneySection(report.Money));
        }

        if (report.Goods.Products.Count > 0)
        {
            sections.Add(GoodsSection(report.Goods));
        }

        if (report.Goods.Lots.Count > 0)
        {
            sections.Add(LotsSection(report.Goods));
        }

        return new ReportDocument(Title, createdAt.ToOffset(BusinessClock.UtcOffset).DateTime, FilterLines(report), sections, cooperativeName);
    }

    private static List<string> FilterLines(EndOfDayReport report)
    {
        var lines = new List<string> { $"Ngày bán: {report.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}" };
        lines.Add(report.Filters.Zones.Count == 0 ? "Vùng: Tất cả vùng" : $"Vùng: {string.Join(", ", report.Filters.Zones.Select(z => z.Name))}");
        if (report.Filters.Method != "all")
        {
            lines.Add($"Phương thức thanh toán: {FundName(report.Filters.Method)}");
        }

        if (report.Filters.CreatedBy is not null)
        {
            lines.Add("Người tạo: một tài khoản đã chọn");
        }

        return lines;
    }

    private static ReportSection SalesSection(EndOfDaySales sales)
    {
        ReportColumn[] columns =
        [
            new("Mã chứng từ", 14, IsCode: true),
            new("Khách hàng", 30),
            new("Vùng", 18),
            new("Giờ", 8, ReportAlign.Center),
            new("SL (kg)", 11, ReportAlign.Right, ReportCellFormat.Kilogram),
            new("Tổng tiền hàng", 16, ReportAlign.Right, ReportCellFormat.Integer),
            new("Thực thu", 16, ReportAlign.Right, ReportCellFormat.Integer),
            new("Ghi nợ", 16, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        var t = sales.Totals;
        var rows = new List<ReportRow> { ReportRow.Group($"Hóa đơn: {t.InvoiceCount}", null, null, null, t.Kg, t.Revenue, t.PaidNow, t.Revenue - t.PaidNow) };
        rows.AddRange(sales.Invoices.Select(i => ReportRow.Detail(
            i.Code, i.Customer?.Name ?? "Khách lẻ", i.ZoneName, i.InvoicedAt.ToOffset(BusinessClock.UtcOffset).ToString("HH:mm", CultureInfo.InvariantCulture),
            i.Kg, i.Total, i.PaidNow, i.Total - i.PaidNow)));
        if (sales.Returns.Count > 0)
        {
            rows.Add(ReportRow.Group($"Trả hàng: {t.ReturnCount}", null, null, null, null, -t.ReturnValue, null, null));
            rows.AddRange(sales.Returns.Select(r => ReportRow.Detail(
                r.Code, $"{r.CustomerName ?? "Khách lẻ"} ({r.InvoiceCode})", r.ZoneName, null, null, -r.Value, null, null)));
        }

        rows.Add(ReportRow.Group("Doanh thu thuần", null, null, null, null, t.NetRevenue, null, null));
        return new ReportSection("Bán hàng", columns, rows);
    }

    private static ReportSection MoneySection(EndOfDayMoney money)
    {
        ReportColumn[] columns =
        [
            new("Mã phiếu", 14, IsCode: true),
            new("Loại", 22),
            new("Đối tượng", 26),
            new("Quỹ", 14),
            new("Thu", 16, ReportAlign.Right, ReportCellFormat.Integer),
            new("Chi", 16, ReportAlign.Right, ReportCellFormat.Integer),
            new("Ghi chú", 30),
        ];
        var receipts = money.Entries.Where(e => e.Direction == "Receipt").ToList();
        var payments = money.Entries.Where(e => e.Direction != "Receipt").ToList();
        var rows = new List<ReportRow>();
        if (receipts.Count > 0)
        {
            rows.Add(ReportRow.Group($"Phiếu thu: {receipts.Count}", null, null, null, receipts.Sum(e => e.Amount), null, null));
            rows.AddRange(receipts.Select(e => ReportRow.Detail(e.Code, SourceName(e.Source), e.PartyName, FundName(e.Fund), e.Amount, null, e.Note)));
        }

        if (payments.Count > 0)
        {
            rows.Add(ReportRow.Group($"Phiếu chi: {payments.Count}", null, null, null, null, payments.Sum(e => e.Amount), null));
            rows.AddRange(payments.Select(e => ReportRow.Detail(e.Code, SourceName(e.Source), e.PartyName, FundName(e.Fund), null, e.Amount, e.Note)));
        }

        rows.Add(ReportRow.Group("Tổng thu chi", null, null, null, money.Totals.Receipts, money.Totals.Payments, null));
        rows.AddRange(money.ByFund.Select(f => ReportRow.SubGroup(FundName(f.Fund), null, null, null, f.Receipts, f.Payments, null)));
        rows.Add(ReportRow.Group("Chênh lệch thu - chi", null, null, null, money.Totals.Net, null, null));
        return new ReportSection("Thu chi", columns, rows);
    }

    private static ReportSection GoodsSection(EndOfDayGoods goods)
    {
        ReportColumn[] columns = [new("Mã hàng", 16, IsCode: true), new("Tên hàng", 44), new("SL bán (kg)", 16, ReportAlign.Right, ReportCellFormat.Kilogram)];
        var rows = new List<ReportRow> { ReportRow.Group($"Hàng hóa bán: {goods.Products.Count}", null, goods.Totals.SoldKg) };
        rows.AddRange(goods.Products.Select(p => ReportRow.Detail(p.Code, p.Name, p.Kg)));
        return new ReportSection("Hàng hóa", columns, rows);
    }

    private static ReportSection LotsSection(EndOfDayGoods goods)
    {
        ReportColumn[] columns =
        [
            new("Mã lô", 16, IsCode: true), new("Vùng", 34), new("Giờ cân", 10, ReportAlign.Center), new("Khối lượng cân (kg)", 20, ReportAlign.Right, ReportCellFormat.Kilogram),
        ];
        var rows = new List<ReportRow> { ReportRow.Group($"Lô thu hoạch đã cân: {goods.Totals.LotCount}", null, null, goods.Totals.WeighedKg) };
        rows.AddRange(goods.Lots.Select(l => ReportRow.Detail(
            l.LotCode, l.ZoneName, l.WeighedAt.ToOffset(BusinessClock.UtcOffset).ToString("HH:mm", CultureInfo.InvariantCulture), l.WeighedKg)));
        return new ReportSection("Lô thu hoạch", columns, rows);
    }

    private static string FundName(string fund) => fund switch
    {
        "Cash" => "Tiền mặt",
        "Bank" => "Chuyển khoản",
        "EWallet" => "Ví điện tử",
        _ => fund,
    };

    private static string SourceName(string source) => source switch
    {
        "CustomerPayment" => "Khách trả tiền",
        "CustomerPaidBack" => "Trả lại khách",
        "SupplierPayment" => "Trả nhà cung cấp",
        "SupplierPaidBack" => "Nhà cung cấp hoàn tiền",
        "CashVoucher" => "Phiếu thu chi khác",
        _ => source,
    };
}
