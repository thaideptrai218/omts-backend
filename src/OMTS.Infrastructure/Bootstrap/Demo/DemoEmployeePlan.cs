using OMTS.Domain.Enums;
namespace OMTS.Infrastructure.Bootstrap.Demo;
internal sealed record DemoEmployeePlan(
    string FullName,
    string Phone,
    string? Username,
    StaffRole Role,
    string? GrantZone,
    string? RevokeReason,
    string[] WorkingZones,
    DateOnly Birthday,
    string Ward,
    string BankName,
    string BankAccountNumber,
    DemoLoginKind Login = DemoLoginKind.Normal,
    int? LeftDaysAgo = null);
