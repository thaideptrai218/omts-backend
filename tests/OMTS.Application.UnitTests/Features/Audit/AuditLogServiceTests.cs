using System.Text.Json.Nodes;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.UnitTests.Features.Audit;

public class AuditLogServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid CallerId = Guid.NewGuid();

    private readonly FakeAuditEventRepository _repository = new();

    private AuditLogService CreateService(Guid? callerId) =>
        new(_repository, new FakeCurrentUser(callerId), new FixedTimeProvider(Now));

    [Fact]
    public void Record_stores_actor_time_action_record_values_and_reason()
    {
        var recordId = Guid.NewGuid();
        var zoneId = Guid.NewGuid();

        var auditEvent = CreateService(CallerId).Record(new AuditEntry("AccessGrant.Revoked", "AccessGrant")
        {
            RecordId = recordId,
            RecordCode = "AG-001",
            Before = new { Status = "Effective" },
            After = new { Status = "Revoked" },
            Reason = "Left the zone",
            ZoneId = zoneId,
        });

        Assert.Same(auditEvent, Assert.Single(_repository.Added));
        Assert.Equal(CallerId, auditEvent.ActorAccountId);
        Assert.Equal(Now, auditEvent.OccurredAt);
        Assert.Equal("AccessGrant.Revoked", auditEvent.Action);
        Assert.Equal("AccessGrant", auditEvent.RecordType);
        Assert.Equal(recordId, auditEvent.RecordId);
        Assert.Equal("AG-001", auditEvent.RecordCode);
        Assert.Equal("Left the zone", auditEvent.Reason);
        Assert.Equal(zoneId, auditEvent.ZoneId);
        Assert.Equal("""{"Status":"Effective"}""", auditEvent.Before);
        Assert.Equal("""{"Status":"Revoked"}""", auditEvent.After);
    }

    [Fact]
    public void Record_uses_the_given_actor_when_the_request_is_anonymous()
    {
        // UC03: account recovery happens before login, the actor is the recovered account.
        var recoveredAccountId = Guid.NewGuid();

        var auditEvent = CreateService(callerId: null).Record(
            new AuditEntry("Account.PasswordRecovered", "Account") { ActorAccountId = recoveredAccountId });

        Assert.Equal(recoveredAccountId, auditEvent.ActorAccountId);
    }

    [Fact]
    public void Record_leaves_actor_empty_for_system_actions()
    {
        var auditEvent = CreateService(callerId: null).Record(new AuditEntry("Account.Unlocked", "Account"));

        Assert.Null(auditEvent.ActorAccountId);
        Assert.Null(auditEvent.Before);
        Assert.Null(auditEvent.After);
    }

    [Fact]
    public void Record_excludes_passwords_verification_codes_and_tokens_at_any_depth()
    {
        // NFR-01: passwords, temporary passwords and verification codes are not written to the audit log.
        const string secret = "Secret#Value123";
        var values = new
        {
            DisplayName = "Nguyen Van A",
            Password = secret,
            TemporaryPassword = secret,
            PasswordHash = secret,
            NewPassword = secret,
            OtpCode = secret,
            OTP = secret,
            VerificationCode = secret,
            verification_code = secret,
            RefreshToken = secret,
            AccessToken = secret,
            ClientSecret = secret,
            Contact = new { Email = "a@example.com", EmailOtp = secret },
            Sessions = new[] { new { Device = "web", RefreshTokenHash = secret } },
            Extra = new Dictionary<string, object> { ["password"] = secret, ["note"] = "kept" },
        };

        var auditEvent = CreateService(CallerId).Record(
            new AuditEntry("Account.Updated", "Account") { Before = values, After = values });

        foreach (var json in new[] { auditEvent.Before, auditEvent.After })
        {
            Assert.NotNull(json);
            Assert.DoesNotContain(secret, json);
            var node = JsonNode.Parse(json)!;
            Assert.Equal(["DisplayName", "Contact", "Sessions", "Extra"], node.AsObject().Select(p => p.Key));
            Assert.Equal(["Email"], node["Contact"]!.AsObject().Select(p => p.Key));
            Assert.Equal(["Device"], node["Sessions"]![0]!.AsObject().Select(p => p.Key));
            Assert.Equal(["note"], node["Extra"]!.AsObject().Select(p => p.Key));
        }
    }

    [Fact]
    public void Record_excludes_the_password_hash_when_an_entity_is_passed()
    {
        var account = new Account
        {
            DisplayName = "Owner",
            Username = "owner",
            PasswordHash = "AQAAAAIAAYagAAAAEHashValue",
            MustChangePassword = true,
            TemporaryPasswordIssuedAt = Now,
        };

        var auditEvent = CreateService(CallerId).Record(new AuditEntry("Account.Created", "Account") { After = account });

        var after = JsonNode.Parse(auditEvent.After!)!.AsObject();
        Assert.DoesNotContain(account.PasswordHash, auditEvent.After);
        Assert.False(after.ContainsKey(nameof(Account.PasswordHash)));
        Assert.Equal("owner", (string?)after[nameof(Account.Username)]);
        // A flag and an issue time about a password are not secrets (UC11 branch 1d, UC72).
        Assert.True((bool?)after[nameof(Account.MustChangePassword)]);
        Assert.True(after.ContainsKey(nameof(Account.TemporaryPasswordIssuedAt)));
    }

    [Theory]
    [InlineData("Footprint")]
    [InlineData("Tokenomics")]
    [InlineData("RecordCode")]
    public void Record_keeps_fields_that_only_look_like_secrets(string name)
    {
        var values = new Dictionary<string, object> { [name] = "value" };

        var auditEvent = CreateService(CallerId).Record(new AuditEntry("Raft.Updated", "Raft") { After = values });

        Assert.True(JsonNode.Parse(auditEvent.After!)!.AsObject().ContainsKey(name));
    }

    [Theory]
    [InlineData("", "Account")]
    [InlineData("Account.Updated", " ")]
    public void Record_requires_action_and_record_type(string action, string recordType)
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateService(CallerId).Record(new AuditEntry(action, recordType)));
        Assert.Empty(_repository.Added);
    }
}
