namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>Which login branch a demo staff account is meant to land in.</summary>
internal enum DemoLoginKind
{
    /// <summary>Verified phone, own password: goes straight in.</summary>
    Normal,

    /// <summary>Temporary password that never expires in the demo: must change it at first login.</summary>
    MustChangePassword,

    /// <summary>Phone not verified yet: the phone verification step comes first.</summary>
    PhoneUnverified,
}

/// <param name="Username">Null for an employee without a login account.</param>
/// <param name="GrantZone">Zone code of the zone–role grant; null = All zones.</param>
/// <param name="GrantFromDays">Days from today the grant starts (negative = in the past).</param>
/// <param name="GrantToDays">Days from today the grant ends; null = no end.</param>
internal sealed record DemoEmployeePlan(
    string FullName,
    string Phone,
    string? Username,
    string RoleName,
    string? GrantZone,
    int GrantFromDays,
    int? GrantToDays,
    string[] WorkingZones,
    DateOnly Birthday,
    string Ward,
    string BankName,
    string BankAccountNumber,
    DemoLoginKind Login = DemoLoginKind.Normal,
    int? LeftDaysAgo = null);

/// <summary>The eight demo employees (LAB-01); phones come from the fake range 0900 000 0xx.</summary>
internal static class DemoEmployeePlans
{
    public const string ZoneManagerRole = "Zone Manager";
    public const string AccountantRole = "Accountant";

    private static readonly string[] ZoneOne = [DemoFarmingZoneStep.ZoneOne];
    private static readonly string[] ZoneTwo = [DemoFarmingZoneStep.ZoneTwo];

    public static readonly IReadOnlyList<DemoEmployeePlan> All =
    [
        new("Lê Minh Tuấn", "0900000011", "quanlyvung1", ZoneManagerRole, DemoFarmingZoneStep.ZoneOne, -90, null,
            ZoneOne, new(1988, 3, 14), "Thị trấn Cái Rồng", "Vietcombank", "1900000011"),
        new("Phạm Thị Lan", "0900000012", "quanlyvung2", ZoneManagerRole, DemoFarmingZoneStep.ZoneTwo, -90, null,
            ZoneTwo, new(1991, 7, 22), "Xã Minh Châu", "Agribank", "5000000012"),
        new("Trần Thu Hà", "0900000013", "ketoan", AccountantRole, null, -90, null,
            [.. ZoneOne, .. ZoneTwo], new(1990, 11, 5), "Thị trấn Cái Rồng", "BIDV", "3100000013"),
        new("Đỗ Văn Cường", "0900000014", "nhanvien.doimk", ZoneManagerRole, DemoFarmingZoneStep.ZoneOne, -30, null,
            ZoneOne, new(1993, 5, 18), "Xã Đoàn Kết", "Vietinbank", "1070000014", DemoLoginKind.MustChangePassword),
        new("Vũ Thị Mai", "0900000015", "nhanvien.chuaxacthuc", ZoneManagerRole, DemoFarmingZoneStep.ZoneTwo, -30, null,
            ZoneTwo, new(1996, 12, 2), "Xã Minh Châu", "MB Bank", "0900000015", DemoLoginKind.PhoneUnverified),
        new("Hoàng Văn Sơn", "0900000016", "nhanvien.hethan", ZoneManagerRole, DemoFarmingZoneStep.ZoneOne, -120, -10,
            ZoneOne, new(1989, 9, 27), "Xã Ngọc Vừng", "Agribank", "5000000016"),
        new("Nguyễn Văn Phúc", "0900000017", null, ZoneManagerRole, null, 0, null,
            ZoneOne, new(1995, 1, 30), "Xã Ngọc Vừng", "Techcombank", "1900000017"),
        new("Bùi Văn Thắng", "0900000018", null, ZoneManagerRole, null, 0, null,
            ZoneTwo, new(1985, 9, 9), "Xã Minh Châu", "Vietcombank", "1900000018", LeftDaysAgo: 20),
    ];
}
