using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Bootstrap.Demo;

internal enum DemoReceiptOutcome
{
    Draft,
    Completed,

    /// <summary>Completed first, then cancelled with a reason (its payments are cancelled too).</summary>
    Cancelled,
}

internal sealed record DemoReceiptLine(string Item, decimal Quantity, decimal UnitPrice, string? SupplierLotCode = null, string? ActualSource = null);

/// <summary>A payment recorded after the receipt (INP-06), <paramref name="DaysAgo"/> days before today.</summary>
internal sealed record DemoLaterPayment(int DaysAgo, decimal Amount, PaymentMethod Method);

/// <summary>A partial return (INP-05) of one line: refund plus debt reduction equal the returned value.</summary>
internal sealed record DemoReturn(string Item, decimal Quantity, int DaysAgo, decimal Refund, decimal DebtReduction, string Reason);

/// <param name="Code">Business code of the demo receipt, kept at the start of its notes: the service generates the receipt's own code from today's date, so this is what makes the seed repeatable.</param>
/// <param name="RaftNumber">Raft of the receipt's zone (1…10); needed when a line is raft material.</param>
internal sealed record DemoReceiptPlan(
    string Code,
    string SupplierPhone,
    string Zone,
    int? RaftNumber,
    int DaysAgo,
    DemoReceiptOutcome Outcome,
    string Note,
    DemoReceiptLine[] Lines,
    decimal Discount = 0,
    decimal PaidNow = 0,
    PaymentMethod PaidNowMethod = PaymentMethod.Cash,
    DemoLaterPayment[]? Later = null,
    DemoReturn? Return = null,
    string? CancelReason = null);

/// <summary>
/// Twenty purchase receipts over the last 85 days: 16 Completed (paid in full, partly or not at all, one
/// with a partial return), 2 Cancelled and 2 Draft. Amounts are whole thousands of VND.
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

    public static readonly IReadOnlyList<DemoReceiptPlan> All =
    [
        new("DEMO-R01", Hatchery, D1, null, 85, Done, "Nhập giống vụ xuân",
            [new("SEED-01", 20000, 900, "LOT-HG-0801", "Cái Rồng"), new("SEED-02", 5000, 1500, "LOT-HG-0802", "Cái Rồng")],
            Discount: 500_000, PaidNow: 10_000_000, PaidNowMethod: PaymentMethod.BankTransfer,
            Later: [new(70, 15_000_000, PaymentMethod.BankTransfer)]),
        new("DEMO-R02", SeedFarm, D2, null, 80, Done, "Nhập giống bản địa",
            [new("SEED-03", 30000, 700, "LOT-BL-0805", "Bạch Long")], PaidNow: 21_000_000),
        new("DEMO-R03", Company, D1, 1, 75, Done, "Phao và dây cho bè B1-01",
            [new("MAT-01", 20, 380_000), new("MAT-02", 6, 650_000)],
            Discount: 100_000, PaidNow: 4_000_000, Later: [new(60, 3_000_000, PaymentMethod.Cash)]),
        new("DEMO-R04", Company, D2, 1, 70, Done, "Lưới bao hàu cho bè B2-01",
            [new("MAT-03", 400, 12_000), new("MAT-06", 30, 48_000)],
            Later: [new(55, 2_000_000, PaymentMethod.BankTransfer)],
            Return: new("MAT-03", 50, 40, 300_000, 300_000, "Lưới bị rách mép, trả lại nhà cung cấp")),
        new("DEMO-R05", Shop, D1, 2, 65, Done, "Gỗ và cọc neo bè B1-02",
            [new("MAT-04", 40, 220_000), new("MAT-05", 8, 450_000)],
            Discount: 400_000, PaidNow: 12_000_000, PaidNowMethod: PaymentMethod.BankTransfer),
        new("DEMO-R06", Store, D1, null, 60, Done, "Dầu và găng tay tháng 8",
            [new("OTH-01", 300, 21_000), new("OTH-02", 40, 18_000)], Discount: 20_000, PaidNow: 7_000_000),
        new("DEMO-R07", Hatchery, D2, null, 55, Done, "Nhập giống Cổ Trai",
            [new("SEED-04", 15000, 1200, "LOT-HG-0820", "Cái Rồng")],
            Discount: 1_000_000, PaidNow: 5_000_000, Later: [new(40, 6_000_000, PaymentMethod.BankTransfer)]),
        new("DEMO-R08", SeedFarm, D1, null, 50, Done, "Nhập giống, chưa thanh toán",
            [new("SEED-01", 12000, 900, "LOT-BL-0829", "Bạch Long"), new("SEED-03", 8000, 700, "LOT-BL-0830", "Bạch Long")],
            Discount: 400_000),
        new("DEMO-R09", Company, D2, 2, 45, Done, "Phao và cọc neo bè B2-02",
            [new("MAT-01", 10, 380_000), new("MAT-05", 6, 450_000)], PaidNow: 6_500_000, PaidNowMethod: PaymentMethod.BankTransfer),
        new("DEMO-R10", Shop, D1, 3, 40, Done, "Gỗ và dây buộc bè B1-03",
            [new("MAT-04", 25, 220_000), new("MAT-06", 20, 48_000)],
            Discount: 460_000, Later: [new(20, 2_500_000, PaymentMethod.Cash)]),
        new("DEMO-R11", Store, D2, null, 38, Done, "Áo phao, vôi, thùng xốp",
            [new("OTH-03", 12, 250_000), new("OTH-04", 200, 6_000), new("OTH-05", 60, 35_000)], Discount: 300_000, PaidNow: 6_000_000),
        new("DEMO-R12", Hatchery, D1, null, 30, Done, "Nhập giống 5 cm",
            [new("SEED-02", 8000, 1500, "LOT-HG-0924", "Cái Rồng")],
            PaidNow: 3_000_000, PaidNowMethod: PaymentMethod.BankTransfer, Later: [new(10, 4_000_000, PaymentMethod.BankTransfer)]),
        new("DEMO-R13", SeedFarm, D2, null, 25, Done, "Nhập giống bản địa đợt 2",
            [new("SEED-03", 25000, 700, "LOT-BL-0928", "Bạch Long")], Discount: 500_000, PaidNow: 17_000_000, PaidNowMethod: PaymentMethod.BankTransfer),
        new("DEMO-R14", Company, D1, 4, 20, Done, "Dây và lưới bè B1-04",
            [new("MAT-02", 4, 650_000), new("MAT-03", 300, 12_000)], Discount: 200_000, PaidNow: 2_000_000),
        new("DEMO-R15", Store, D2, null, 12, Done, "Dầu diesel tháng 10",
            [new("OTH-01", 250, 21_000)], PaidNow: 5_250_000),
        new("DEMO-R16", Shop, D2, 3, 8, Done, "Gỗ khung bè B2-03",
            [new("MAT-04", 30, 220_000)], Discount: 600_000),
        new("DEMO-R17", SeedFarm, D1, null, 35, DemoReceiptOutcome.Cancelled, "Nhập nhầm loại giống",
            [new("SEED-01", 6000, 900, "LOT-BL-0918", "Bạch Long")], PaidNow: 2_000_000,
            CancelReason: "Nhập nhầm loại giống, hủy phiếu và nhập lại"),
        new("DEMO-R18", Company, D2, 4, 28, DemoReceiptOutcome.Cancelled, "Cọc neo bè B2-04",
            [new("MAT-05", 10, 450_000)], CancelReason: "Nhà cung cấp giao thiếu hàng"),
        new("DEMO-R19", Hatchery, D1, null, 5, DemoReceiptOutcome.Draft, "Đặt giống đợt tới, chờ xác nhận",
            [new("SEED-04", 10000, 1200)]),
        new("DEMO-R20", Company, D2, 5, 3, DemoReceiptOutcome.Draft, "Phao bè B2-05, chờ báo giá",
            [new("MAT-01", 8, 380_000)]),
    ];
}
