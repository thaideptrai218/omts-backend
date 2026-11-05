using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

internal enum DemoReceiptOutcome
{
    Draft,
    Completed,

    /// <summary>Completed first, then cancelled with a reason (payments are never cancelled with a receipt, BR-PURCHASE-09).</summary>
    Cancelled,
}

internal sealed record DemoReceiptLine(string Item, decimal Quantity, decimal UnitPrice, string? SupplierLotCode = null);

/// <summary>A payment to the supplier of the receipt, <paramref name="DaysAgo"/> days before today; it is not linked to the receipt (BR-PURCHASE-04).</summary>
internal sealed record DemoPayment(int DaysAgo, decimal Amount, PaymentMethod Method);

/// <summary>A partial return (INP-05) of one line: refund plus debt reduction equal the returned value.</summary>
internal sealed record DemoReturn(string Item, decimal Quantity, int DaysAgo, decimal Refund, decimal DebtReduction, string Reason);

/// <param name="Code">Business code of the demo receipt, kept at the start of its notes: the service gives the receipt its own PN code, so this is what makes the seed repeatable.</param>
/// <param name="RaftZone">Zone of <paramref name="RaftNumber"/>.</param>
/// <param name="RaftNumber">Raft (1…10) the Seed and Material lines were bought for (GBR-04, BR-FARM-05); null for none.</param>
internal sealed record DemoReceiptPlan(
    string Code,
    string SupplierPhone,
    int DaysAgo,
    DemoReceiptOutcome Outcome,
    string Note,
    DemoReceiptLine[] Lines,
    string? RaftZone = null,
    int? RaftNumber = null,
    DemoPayment[]? Payments = null,
    DemoReturn? Return = null,
    string? CancelReason = null);

/// <summary>
/// Twenty purchase receipts over the last 85 days: 16 Completed (paid in full, partly or not at all, one with a partial
/// return), 2 Cancelled and 2 Draft. Material lines name the raft they were bought for, and so do two seed receipts. One
/// line has a fractional quantity, so its amount is not a whole thousand (BR-PURCHASE-02).
/// </summary>
internal static class DemoReceiptPlans
{
    private const string D1 = DemoFarmingZoneStep.ZoneOne;
    private const string D2 = DemoFarmingZoneStep.ZoneTwo;
    private const string Hatchery = DemoSupplierStep.SeedHatcheryPhone;
    private const string SeedFarm = DemoSupplierStep.SeedFarmPhone;
    private const string Company = DemoSupplierStep.MaterialCompanyPhone;
    private const string Shop = DemoSupplierStep.MaterialShopPhone;
    private const string Store = DemoSupplierStep.GeneralStorePhone;
    private const DemoReceiptOutcome Done = DemoReceiptOutcome.Completed;
    private const PaymentMethod Cash = PaymentMethod.Cash;
    private const PaymentMethod Transfer = PaymentMethod.BankTransfer;

    public static readonly IReadOnlyList<DemoReceiptPlan> All =
    [
        new("DEMO-R01", Hatchery, 85, Done, "Nhập giống vụ xuân",
            [new("VT001", 20000, 900, "LOT-HG-0801"), new("VT002", 5000, 1500, "LOT-HG-0802")], D1, 1,
            Payments: [new(85, 10_000_000, Transfer), new(70, 15_000_000, Transfer)]),
        new("DEMO-R02", SeedFarm, 80, Done, "Nhập giống bản địa",
            [new("VT003", 30000, 700, "LOT-BL-0805")], Payments: [new(80, 21_000_000, Cash)]),
        new("DEMO-R03", Company, 75, Done, "Phao và dây cho bè B1-01",
            [new("VT005", 20, 380_000), new("VT006", 6, 650_000)], D1, 1,
            Payments: [new(75, 4_000_000, Cash), new(60, 3_000_000, Cash)]),
        new("DEMO-R04", Company, 70, Done, "Lưới bao hàu cho bè B2-01",
            [new("VT007", 400, 12_000), new("VT010", 30, 48_000)], D2, 1,
            Payments: [new(55, 2_000_000, Transfer)],
            Return: new("VT007", 50, 40, 300_000, 300_000, "Lưới bị rách mép, trả lại nhà cung cấp")),
        new("DEMO-R05", Shop, 65, Done, "Gỗ và cọc neo bè B1-02",
            [new("VT008", 40, 220_000), new("VT009", 8, 450_000)], D1, 2, Payments: [new(65, 12_000_000, Transfer)]),
        new("DEMO-R06", Store, 60, Done, "Dầu và găng tay tháng 8",
            [new("VT011", 300.5m, 21_000), new("VT012", 40, 18_000)], Payments: [new(60, 7_000_000, Cash)]),
        new("DEMO-R07", Hatchery, 55, Done, "Nhập giống Cổ Trai",
            [new("VT004", 15000, 1200, "LOT-HG-0820")], D2, 2,
            Payments: [new(55, 5_000_000, Cash), new(40, 6_000_000, Transfer)]),
        new("DEMO-R08", SeedFarm, 50, Done, "Nhập giống, chưa thanh toán",
            [new("VT001", 12000, 900, "LOT-BL-0829"), new("VT003", 8000, 700, "LOT-BL-0830")]),
        new("DEMO-R09", Company, 45, Done, "Phao và cọc neo bè B2-02",
            [new("VT005", 10, 380_000), new("VT009", 6, 450_000)], D2, 2, Payments: [new(45, 6_500_000, Transfer)]),
        new("DEMO-R10", Shop, 40, Done, "Gỗ và dây buộc bè B1-03",
            [new("VT008", 25, 220_000), new("VT010", 20, 48_000)], D1, 3, Payments: [new(20, 2_500_000, Cash)]),
        new("DEMO-R11", Store, 38, Done, "Áo phao, vôi, thùng xốp",
            [new("VT013", 12, 250_000), new("VT014", 200, 6_000), new("VT015", 60, 35_000)], Payments: [new(38, 6_000_000, Cash)]),
        new("DEMO-R12", Hatchery, 30, Done, "Nhập giống 5 cm",
            [new("VT002", 8000, 1500, "LOT-HG-0924")], Payments: [new(30, 3_000_000, Transfer), new(10, 4_000_000, Transfer)]),
        new("DEMO-R13", SeedFarm, 25, Done, "Nhập giống bản địa đợt 2",
            [new("VT003", 25000, 700, "LOT-BL-0928")], Payments: [new(25, 17_000_000, Transfer)]),
        new("DEMO-R14", Company, 20, Done, "Dây và lưới bè B1-04",
            [new("VT006", 4, 650_000), new("VT007", 300, 12_000)], D1, 4, Payments: [new(20, 2_000_000, Cash)]),
        new("DEMO-R15", Store, 12, Done, "Dầu diesel tháng 10",
            [new("VT011", 250, 21_000)], Payments: [new(12, 5_250_000, Cash)]),
        new("DEMO-R16", Shop, 8, Done, "Gỗ khung bè B2-03",
            [new("VT008", 30, 220_000)], D2, 3),
        new("DEMO-R17", SeedFarm, 35, DemoReceiptOutcome.Cancelled, "Nhập nhầm loại giống",
            [new("VT001", 6000, 900, "LOT-BL-0918")], Payments: [new(35, 2_000_000, Cash)],
            CancelReason: "Nhập nhầm loại giống, hủy phiếu và nhập lại"),
        new("DEMO-R18", Company, 28, DemoReceiptOutcome.Cancelled, "Cọc neo bè B2-04",
            [new("VT009", 10, 450_000)], D2, 4, CancelReason: "Nhà cung cấp giao thiếu hàng"),
        new("DEMO-R19", Hatchery, 5, DemoReceiptOutcome.Draft, "Đặt giống đợt tới, chờ xác nhận",
            [new("VT004", 10000, 1200)]),
        new("DEMO-R20", Company, 3, DemoReceiptOutcome.Draft, "Phao bè B2-05, chờ báo giá",
            [new("VT005", 8, 380_000)], D2, 5),
    ];
}
