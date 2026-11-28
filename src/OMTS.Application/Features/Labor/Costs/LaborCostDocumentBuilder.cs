using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Application.Features.Reports.Documents;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Costs;

/// <summary>The labour cost by raft as a printable <see cref="ReportDocument"/>: one table with hours and cost per raft, one with the split by job type, one with the split by employee type and way of pay. Changes nothing.</summary>
public static class LaborCostDocumentBuilder
{
    public static ReportDocument Build(LaborCostByRaft costs, string cooperativeName, DateTimeOffset createdAt)
    {
        ReportColumn[] columns =
        [
            new("Bè", 18), new("Vùng nuôi", 26), new("Giờ công", 12, ReportAlign.Right), new("Chi phí nhân công", 18, ReportAlign.Right, ReportCellFormat.Integer), new("Tỷ trọng", 10, ReportAlign.Right),
        ];
        var rows = new List<ReportRow> { ReportRow.Group($"Tổng: {costs.Items.Count} dòng", null, PayrollRules.HoursText(costs.TotalHours), costs.Total, costs.Total > 0 ? "100%" : null) };
        rows.AddRange(costs.Items.Select(i => ReportRow.Detail(
            i.RaftCode ?? "Công ươm cấp vùng", i.ZoneName, PayrollRules.HoursText(i.Hours), i.Amount, costs.Total > 0 ? Percent(i.Amount, costs.Total) : null)));
        var sections = new List<ReportSection> { new("Chi phí nhân công theo bè", columns, rows) };

        ReportColumn[] jobColumns =
        [
            new("Bè", 18), new("Loại việc", 28), new("Giờ công", 12, ReportAlign.Right), new("Chi phí nhân công", 18, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        var jobRows = new List<ReportRow>();
        foreach (var item in costs.Items.Where(i => i.ByJob is { Count: > 0 }))
        {
            jobRows.Add(ReportRow.SubGroup(item.RaftCode ?? "Công ươm cấp vùng", null, PayrollRules.HoursText(item.Hours), item.Amount));
            jobRows.AddRange(item.ByJob!.Select(j => ReportRow.Detail(null, j.JobName, PayrollRules.HoursText(j.Hours), j.Amount)));
        }

        if (jobRows.Count > 0)
        {
            sections.Add(new ReportSection("Theo loại việc", jobColumns, jobRows));
        }

        ReportColumn[] payColumns =
        [
            new("Bè", 18), new("Nhóm", 30), new("Giờ công", 12, ReportAlign.Right), new("Số ca", 10, ReportAlign.Right), new("Chi phí nhân công", 18, ReportAlign.Right, ReportCellFormat.Integer),
        ];
        var payRows = new List<ReportRow>();
        foreach (var item in costs.Items.Where(i => i.ByType is { Count: > 0 } || i.ByKind is { Count: > 0 }))
        {
            payRows.Add(ReportRow.SubGroup(item.RaftCode ?? "Công ươm cấp vùng", null, PayrollRules.HoursText(item.Hours), ShiftsText(item.Shifts), item.Amount));
            payRows.AddRange((item.ByType ?? []).Select(t => ReportRow.Detail(null, TypeText(t.EmployeeType), PayrollRules.HoursText(t.Hours), ShiftsText(t.Shifts), t.Amount)));
            payRows.AddRange((item.ByKind ?? []).Select(k => ReportRow.Detail(null, KindText(k.Kind), PayrollRules.HoursText(k.Hours), ShiftsText(k.Shifts), k.Amount)));
        }

        if (payRows.Count > 0)
        {
            sections.Add(new ReportSection("Theo nhóm nhân sự và cách trả công", payColumns, payRows));
        }

        var filters = new List<string> { $"Từ ngày {Day(costs.From)} đến ngày {Day(costs.To)}" };
        return new ReportDocument("Chi phí nhân công theo bè", createdAt.ToOffset(BusinessClock.UtcOffset).DateTime, filters, sections, cooperativeName, ReportOrientation.Landscape);
    }

    public static string TypeText(EmployeeType type) => type == EmployeeType.Staff ? "Nội bộ" : "Thời vụ";

    public static string KindText(PayLineKind kind) => kind switch
    {
        PayLineKind.Shift => "Trả theo ca",
        PayLineKind.FieldShift => "Ca ngoài đồng của lương cố định",
        _ => "Trả theo giờ",
    };

    private static string ShiftsText(decimal shifts) => shifts > 0 ? shifts.ToString("0.##", CultureInfo.GetCultureInfo("vi-VN")) : string.Empty;

    private static string Day(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Percent(decimal part, decimal total) => (part / total * 100).ToString("0.#", CultureInfo.GetCultureInfo("vi-VN")) + "%";
}