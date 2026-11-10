using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Infrastructure.Identity;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-07 and BR-LABOR-06: the attendance QR code is signed, names its employee and works for 60 seconds only.</summary>
public class AttendanceQrCodeTests
{
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (HmacAttendanceQrCodes Codes, MovableClock Clock) Create(string? key = null)
    {
        var options = Options.Create(new JwtOptions { SigningKey = key ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) });
        var clock = new MovableClock(new DateTimeOffset(2026, 11, 11, 3, 0, 0, TimeSpan.Zero));
        return (new HmacAttendanceQrCodes(options, clock), clock);
    }

    [Fact]
    public void A_code_names_its_employee_until_it_expires_after_60_seconds()
    {
        var (codes, clock) = Create();
        var employee = Guid.NewGuid();

        var code = codes.Issue(employee);

        Assert.Equal(clock.Now.AddSeconds(60), code.ExpiresAt);
        Assert.Equal(employee, codes.Read(code.Code));
        clock.Now = clock.Now.AddSeconds(59);
        Assert.Equal(employee, codes.Read(code.Code));
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Null(codes.Read(code.Code));
    }

    [Fact]
    public void A_changed_unsigned_or_foreign_code_is_not_read()
    {
        var (codes, _) = Create();
        var (other, _) = Create();
        var employee = Guid.NewGuid();
        var code = codes.Issue(employee).Code;
        var parts = code.Split('.');

        Assert.Null(codes.Read($"{parts[0]}.{Guid.NewGuid():N}.{parts[2]}.{parts[3]}"));
        Assert.Null(codes.Read($"{parts[0]}.{parts[1]}.{long.Parse(parts[2]) + 3600}.{parts[3]}"));
        Assert.Null(codes.Read(code[..^1] + (code[^1] == '0' ? '1' : '0')));
        Assert.Null(codes.Read($"{parts[0]}.{parts[1]}.{parts[2]}"));
        Assert.Null(other.Read(code));
        Assert.Null(codes.Read(null));
        Assert.Null(codes.Read(""));
        Assert.Null(codes.Read("https://example.com/qr"));
        Assert.Equal(employee, codes.Read($" {code} "));
    }

    [Fact]
    public void Two_codes_of_the_same_employee_are_different_a_minute_apart()
    {
        var (codes, clock) = Create();
        var employee = Guid.NewGuid();

        var first = codes.Issue(employee).Code;
        clock.Now = clock.Now.AddSeconds(60);

        Assert.NotEqual(first, codes.Issue(employee).Code);
    }
}
