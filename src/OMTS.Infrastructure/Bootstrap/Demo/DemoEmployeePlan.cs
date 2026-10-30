
namespace OMTS.Infrastructure.Bootstrap.Demo;

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
