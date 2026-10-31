using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.UnitTests.Features.Audit;

/// <summary>
/// QA of OMTS-216 PR 0.6 (GBR-02, BR-LABOR-02, NFR-01): the secrets the audit log must never hold, as a use case would
/// pass them to <see cref="AuditLogService.Record"/>.
/// </summary>
public class AuditSecretMaskingQaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeAuditEventRepository _repository = new();

    // LABOR-03 issues 6-digit login codes for the employee app; GBR-02 names them among the values never written.
    [Fact]
    public void Record_never_writes_a_login_code_of_the_employee_app()
    {
        var code = Random.Shared.Next(100_000, 1_000_000).ToString();

        var e = Record(new { EmployeeId = Guid.NewGuid(), LoginCode = code, Device = new { login_code = code } });

        Assert.DoesNotContain(code, e.After);
        Assert.Contains("EmployeeId", e.After);
    }

    [Fact]
    public void Record_never_writes_a_field_that_holds_several_secrets()
    {
        var secret = Guid.NewGuid().ToString("N");

        var e = Record(new { Name = "kept", RefreshTokens = new[] { secret }, Passwords = new[] { secret }, Credentials = new[] { secret } });

        Assert.DoesNotContain(secret, e.After);
        Assert.Contains("kept", e.After);
    }

    private AuditEvent Record(object after) =>
        new AuditLogService(_repository, new FakeCurrentUser(Guid.NewGuid()), new FakeStatusCatalog(), new FixedTimeProvider(Now))
            .Record(new AuditEntry("Employee.Updated", "Employee") { After = after });
}
