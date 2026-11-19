using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Features.Reports.Documents;

namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>
/// Lays the end-of-day report out as a printable <see cref="ReportDocument"/> in the KiotViet style (PDF and Excel share it). The title and the tables follow the concern: sales (a group per
/// kind of document, one row per document), money (receipts and payments with the totals by fund), goods (one row per product, with the documents under it when <c>expanded</c>, and the lots
/// weighed) or the summary of the three. The filter lines name what was chosen. Changes nothing.
/// </summary>
public static class EndOfDayDocumentBuilder
{
    private const string Time = "HH:mm";
    private const string DayAndTime = "dd/MM HH:mm";
    private const string FullTime = "dd/MM/yyyy HH:mm";

    public static string Title(EndOfDayConcern concern) => concern switch
    {
        EndOfDayConcern.Money => "Báo cáo cuối ngày về thu chi",
        EndOfDayConcern.Goods => "Báo cáo cuối ngày về hàng hóa",
        EndOfDayConcern.Summary => "Báo cáo cuối ngày về tổng hợp",
        _ => "Báo cáo cuối ngày về bán hàng",
    };

    /// <summary>BaoCaoCuoiNgay_{BanHang|ThuChi|HangHoa|TongHop}_{yyyyMMdd} and, when the period ends on another day, -{yyyyMMdd} of that day.</summary>
    public static string FileBaseName(EndOfDayConcern concern, EndOfDayPeriod period)
    {
        var name = concern switch
        {
            EndOfDayConcern.Money => "ThuChi",
            EndOfDayConcern.Goods => "HangHoa",
            EndOfDayConcern.Summary => "TongHop",
            _ => "BanHang",
        };
        var from = period.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return period.SpansDays ? $"BaoCaoCuoiNgay_{name}_{from}-{period.To.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}" : $"BaoCaoCuoiNgay_{name}_{from}";
    }

    public static ReportDocument Build(
        EndOfDayReport report, EndOfDayPeriod period, string cooperativeName, DateTimeOffset createdAt, ReportOrientation orientation, bool expanded)
    {
        var concern = Enum.Parse<EndOfDayConcern>(report.Concern, ignoreCase: true);
        var sections = new List<ReportSection>();
        switch (concern)
        {
            case EndOfDayConcern.Money:
                sections.Add(MoneySection(report.Money!, period));
                break;
            case EndOfDayConcern.Goods:
                sections.Add(GoodsSection(report.Goods!, period, expanded));
                if (report.Goods!.Lots.Count > 0)
                {
                    sections.Add(LotsSection(report.Goods));
                }

                break;
            case EndOfDayConcern.Summary:
                sections.AddRange(SummarySections(report.Summary!));
                break;
            default:
                sections.Add(SalesSection(report.Sales!, period));
                break;
        }

        return new ReportDocument(
            Title(concern), createdAt.ToOffset(BusinessClock.UtcOffset).DateTime, FilterLines(report, period, concern), sections, cooperativeName, orientation);
    }

    private static List<string> FilterLines(EndOfDayReport report, EndOfDayPeriod period, EndOfDayConcern concern)
    {
        var f = report.Filters;
        var lines = new List<string>
        {
            $"Thời gian: {period.From.ToString(FullTime, CultureInfo.InvariantCulture)} - {period.To.ToString(FullTime, CultureInfo.InvariantCulture)}",
            f.Zones.Count == 0 ? "Vùng nuôi: Tất cả vùng" : $"Vùng nuôi: {Join(f.Zones)}",
        };
        void Add(string label, IEnumerable<string> values)
        {
            var text = string.Join(", ", values);
            if (text.Length > 0)
            {
                lines.Add($"{label}: {text}");
            }
        }

        switch (concern)
        {
            case EndOfDayConcern.Sales:
                Add("Khách hàng", f.Customers.Select(c => c.Name));
                Add("Người nhận đặt", f.ReceivedBy.Select(c => c.Name));
                Add("Người tạo", f.CreatedBy.Select(c => c.Name));
                Add("Phương thức thanh toán", f.PaymentMethods.Select(PaymentMethodName));
                Add("Kênh bán", f.Channels.Select(ChannelName));
                Add("Hình thức thanh toán", f.PaymentTerms.Select(TermName));
                break;
            case EndOfDayConcern.Money:
                Add("Quỹ", f.Funds.Select(FundName));
                Add("Nguồn", f.Sources.Select(SourceName));
                Add("Loại thu chi", f.Types.Select(c => c.Name));
                Add("Đối tượng", f.PartyKinds.Select(PartyKindName));
                Add("Người tạo", f.CreatedBy.Select(c => c.Name));
                break;
            case EndOfDayConcern.Goods:
                Add("Nhóm hàng", f.Categories.Select(c => c.Name));
                Add("Hàng hóa", f.Products.Select(c => c.Name));
                Add("Khách hàng", f.Customers.Select(c => c.Name));
                Add("Người tạo", f.CreatedBy.Select(c => c.Name));
                break;
            default:
                Add("Người tạo", f.CreatedBy.Select(c => c.Name));
                break;
        }

        return lines;
    }

    private static string Join(IEnumerable<ReportChoice> choices) => string.Join(", ", choices.Select(c => c.Name));

    private static string At(DateTimeOffset at, EndOfDayPeriod period) =>
        at.ToOffset(BusinessClock.UtcOffset).ToString(period.SpansDays ? DayAndTime : Time, CultureInfo.InvariantCulture);

    private static decimal? OrNull(decimal value) => value == 0m ? null : value;

    private static ReportSection SalesSection(EndOfDaySales sales, EndOfDayPeriod period)
    {
        ReportColumn[] columns =
        [
            new("Mã giao dịch", 18, IsCode: true),
            new("Thời gian", 14),
            new("SL (kg)", 12, ReportAlign.Right, ReportCellFormat.Kilogram),
            new("Doanh thu", 16, ReportAlign.Right, ReportCellFormat.Integer),
            new("Giảm giá", 14, ReportAlign.Right, ReportCellFormat.Integer),
            new("Thu khác", 14, ReportAlign.Right, ReportCellFormat.Integer),
            new("Thực thu", 16, ReportAlign.Right, ReportCellFormat.Integer),
            new("Còn nợ", 14, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        var rows = new List<ReportRow>();
        foreach (var group in sales.Groups.Where(g => g.Count > 0))
        {
            var isInvoice = group.Kind == EndOfDayCalculator.InvoiceKind;
            rows.Add(ReportRow.Group(
                $"{KindName(group.Kind)}: {group.Count}", null, group.QuantityKg, group.Revenue, OrNull(group.Discount), isInvoice ? OrNull(group.OtherCharges) : null, OrNull(group.Paid),
                isInvoice ? group.Debt : null));
            rows.AddRange(group.Rows.Select(r => ReportRow.Detail(
                r.Code, At(r.At, period), r.QuantityKg, r.Revenue, OrNull(r.Discount), isInvoice ? OrNull(r.OtherCharges) : null, OrNull(r.Paid), isInvoice ? r.Debt : null)));
        }

        var t = sales.Totals;
        rows.Add(ReportRow.Group("Tổng cộng", null, t.QuantityKg, t.Revenue, t.Discount, t.OtherCharges, t.Paid, t.Debt));
        rows.Add(ReportRow.Group("Doanh thu thuần", null, null, t.NetRevenue, null, null, null, null));
        return new ReportSection(
            "Bán hàng", columns, rows, "Đặt hàng không tính vào doanh thu tổng; thực thu của đặt hàng là tiền cọc nhận trong kỳ. Doanh thu thuần = doanh thu - giảm giá, trả hàng tính âm.");
    }

    private static ReportSection MoneySection(EndOfDayMoney money, EndOfDayPeriod period)
    {
        ReportColumn[] columns =
        [
            new("Mã phiếu", 14, IsCode: true),
            new("Thời gian", 13),
            new("Loại thu chi", 22),
            new("Đối tượng", 24),
            new("Quỹ", 13),
            new("Thu", 15, ReportAlign.Right, ReportCellFormat.Integer),
            new("Chi", 15, ReportAlign.Right, ReportCellFormat.Integer),
            new("Ghi chú", 24),
        ];
        var receipts = money.Entries.Where(e => e.Direction == "Receipt").ToList();
        var payments = money.Entries.Where(e => e.Direction != "Receipt").ToList();
        var rows = new List<ReportRow>();
        if (receipts.Count > 0)
        {
            rows.Add(ReportRow.Group($"Phiếu thu: {receipts.Count}", null, null, null, null, receipts.Sum(e => e.Amount), null, null));
            rows.AddRange(receipts.Select(e => ReportRow.Detail(e.Code, At(e.At, period), e.TypeName ?? SourceName(e.Source), e.PartyName, FundName(e.Fund), e.Amount, null, e.Note)));
        }

        if (payments.Count > 0)
        {
            rows.Add(ReportRow.Group($"Phiếu chi: {payments.Count}", null, null, null, null, null, payments.Sum(e => e.Amount), null));
            rows.AddRange(payments.Select(e => ReportRow.Detail(e.Code, At(e.At, period), e.TypeName ?? SourceName(e.Source), e.PartyName, FundName(e.Fund), null, e.Amount, e.Note)));
        }

        rows.Add(ReportRow.Group("Tổng thu chi", null, null, null, null, money.Totals.Receipts, money.Totals.Payments, null));
        rows.AddRange(money.ByFund.Select(f => ReportRow.SubGroup(FundName(f.Fund), null, null, null, null, f.Receipts, f.Payments, null)));
        rows.Add(ReportRow.Group("Chênh lệch thu - chi", null, null, null, null, money.Totals.Net, null, null));
        return new ReportSection("Thu chi", columns, rows);
    }

    private static ReportSection GoodsSection(EndOfDayGoods goods, EndOfDayPeriod period, bool expanded)
    {
        ReportColumn[] columns =
        [
            new("Mã hàng", 16, IsCode: true),
            new("Tên hàng", 30),
            new("SL bán", 12, ReportAlign.Right, ReportCellFormat.Kilogram),
            new("Doanh thu", 15, ReportAlign.Right, ReportCellFormat.Integer),
            new("SL trả", 12, ReportAlign.Right, ReportCellFormat.Kilogram),
            new("Giá trị trả", 15, ReportAlign.Right, ReportCellFormat.Integer),
            new("Doanh thu thuần", 16, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        var t = goods.Totals;
        var rows = new List<ReportRow> { ReportRow.Group($"Hàng hóa: {goods.Products.Count}", null, t.SoldKg, t.Revenue, t.ReturnedKg, t.ReturnValue, t.NetRevenue) };
        foreach (var p in goods.Products)
        {
            rows.Add(expanded
                ? ReportRow.SubGroup(p.Code, p.Name, p.SoldKg, p.Revenue, p.ReturnedKg, p.ReturnValue, p.NetRevenue)
                : ReportRow.Detail(p.Code, p.Name, p.SoldKg, p.Revenue, p.ReturnedKg, p.ReturnValue, p.NetRevenue));
            if (!expanded)
            {
                continue;
            }

            rows.AddRange(p.Details.Select(d =>
            {
                var label = $"{(d.Kind == EndOfDayCalculator.ReturnKind ? "Trả hàng" : "Hóa đơn")} {At(d.At, period)}{(string.IsNullOrEmpty(d.CustomerName) ? string.Empty : $" - {d.CustomerName}")}";
                return d.Kind == EndOfDayCalculator.ReturnKind
                    ? ReportRow.Detail(d.Code, label, null, null, d.Kg, d.Value, null)
                    : ReportRow.Detail(d.Code, label, d.Kg, d.Value, null, null, null);
            }));
        }

        return new ReportSection("Hàng hóa", columns, rows, "(Đã phân bổ giảm giá hóa đơn)");
    }

    private static ReportSection LotsSection(EndOfDayGoods goods)
    {
        ReportColumn[] columns =
        [
            new("Mã lô", 16, IsCode: true), new("Vùng", 34), new("Giờ cân", 14, ReportAlign.Center), new("Khối lượng cân (kg)", 20, ReportAlign.Right, ReportCellFormat.Kilogram),
        ];
        var rows = new List<ReportRow> { ReportRow.Group($"Lô thu hoạch đã cân: {goods.Totals.LotCount}", null, null, goods.Totals.WeighedKg) };
        rows.AddRange(goods.Lots.Select(l => ReportRow.Detail(
            l.LotCode, l.ZoneName, l.WeighedAt.ToOffset(BusinessClock.UtcOffset).ToString(DayAndTime, CultureInfo.InvariantCulture), l.WeighedKg)));
        return new ReportSection("Lô thu hoạch", columns, rows);
    }

    private static IEnumerable<ReportSection> SummarySections(EndOfDaySummary summary)
    {
        var s = summary.Sales;
        ReportColumn[] salesColumns =
        [
            new("Chỉ tiêu", 34), new("Số lượng", 14, ReportAlign.Right, ReportCellFormat.Integer), new("Giá trị (đ)", 22, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        yield return new ReportSection(
            "Bán hàng",
            salesColumns,
            [
                ReportRow.Detail("Đặt hàng", s.OrderCount, s.OrderValue),
                ReportRow.Detail("Tiền cọc nhận trong kỳ", null, s.Deposits),
                ReportRow.Detail("Hóa đơn", s.InvoiceCount, s.Revenue),
                ReportRow.Detail("Giảm giá", null, s.Discount),
                ReportRow.Detail("Trả hàng", s.ReturnCount, s.ReturnValue),
                ReportRow.Group("Doanh thu thuần", null, s.NetRevenue),
                ReportRow.Detail("Thực thu", null, s.Paid),
                ReportRow.Detail("Còn nợ", null, s.Debt),
            ],
            "Đặt hàng không tính vào doanh thu; thực thu gồm cả tiền cọc.");

        if (summary.Money.ByFund.Count > 0)
        {
            ReportColumn[] moneyColumns =
            [
                new("Quỹ", 30),
                new("Thu", 18, ReportAlign.Right, ReportCellFormat.Integer),
                new("Chi", 18, ReportAlign.Right, ReportCellFormat.Integer),
                new("Chênh lệch", 18, ReportAlign.Right, ReportCellFormat.Integer),
            ];
            var moneyRows = summary.Money.ByFund.Select(f => ReportRow.Detail(FundName(f.Fund), f.Receipts, f.Payments, f.Net)).ToList();
            moneyRows.Add(ReportRow.Group("Tổng thu chi", summary.Money.Totals.Receipts, summary.Money.Totals.Payments, summary.Money.Totals.Net));
            yield return new ReportSection("Thu chi", moneyColumns, moneyRows);
        }

        var g = summary.Goods;
        ReportColumn[] goodsColumns =
        [
            new("Chỉ tiêu", 34), new("Số lượng", 14, ReportAlign.Right, ReportCellFormat.Integer), new("Khối lượng (kg)", 22, ReportAlign.Right, ReportCellFormat.Kilogram),
        ];
        yield return new ReportSection(
            "Hàng hóa",
            goodsColumns,
            [
                ReportRow.Detail("Mặt hàng đã bán", g.ProductCount, g.SoldKg),
                ReportRow.Detail("Hàng trả lại", null, g.ReturnedKg),
                ReportRow.Detail("Lô thu hoạch đã cân", g.LotCount, g.WeighedKg),
            ]);
    }

    private static string KindName(string kind) => kind switch
    {
        EndOfDayCalculator.OrderKind => "Đặt hàng",
        EndOfDayCalculator.ReturnKind => "Trả hàng",
        _ => "Hóa đơn",
    };

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

    private static string PaymentMethodName(string method) => method switch
    {
        "Cash" => "Tiền mặt",
        "VietQrInvoice" => "VietQR hóa đơn",
        "OtherTransfer" => "Chuyển khoản khác",
        "EWallet" => "Ví điện tử",
        _ => method,
    };

    private static string ChannelName(string channel) => channel switch
    {
        "App" => "Ứng dụng",
        "Phone" => "Điện thoại",
        "AtPort" => "Tại cảng",
        _ => channel,
    };

    private static string TermName(string term) => term switch
    {
        "PayNow" => "Trả ngay",
        "PayLater" => "Ghi nợ",
        _ => term,
    };

    private static string PartyKindName(string kind) => kind switch
    {
        "Customer" => "Khách hàng",
        "Supplier" => "Nhà cung cấp",
        "Employee" => "Nhân viên",
        "Other" => "Khác",
        _ => kind,
    };
}
