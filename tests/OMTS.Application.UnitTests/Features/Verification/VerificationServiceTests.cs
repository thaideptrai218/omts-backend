using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Verification;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Verification;

public partial class VerificationServiceTests
{
    private const string Phone = "0912345678";
    private static readonly Guid AccountId = Guid.NewGuid();

    private readonly FakeCodes _codes = new();
    private readonly FakeSms _sms = new();
    private readonly MutableTime _time = new(new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.Zero));

    private VerificationService Service => new(
        _codes,
        new VerificationThrottle(_codes, Options.Create(new VerificationLimitOptions()), NullLogger<VerificationThrottle>.Instance),
        new Sha256Hasher(),
        _sms,
        new NoEmail(),
        new NoSave(),
        new VerificationStartGate(),
        _time);

    [Fact]
    public async Task Code_is_valid_until_5_minutes()
    {
        var started = await StartAsync();
        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.True((await Service.VerifyAsync(started, _sms.LastCode, AccountId, default)).IsSuccess);

        var second = await StartAsync(VerificationPurpose.ChangePhone);
        _time.Advance(TimeSpan.FromMinutes(5));
        AssertWrongCode(await Service.VerifyAsync(second, _sms.LastCode, AccountId, default));
    }

    [Fact]
    public async Task Fifth_wrong_entry_locks_the_request()
    {
        var started = await StartAsync();
        for (var i = 0; i < 4; i++)
        {
            AssertWrongCode(await Service.VerifyAsync(started, "000000", AccountId, default));
        }

        Assert.True((await Service.VerifyAsync(started, _sms.LastCode, AccountId, default)).IsSuccess);

        var locked = await StartAsync(VerificationPurpose.ChangePhone);
        for (var i = 0; i < 4; i++)
        {
            AssertWrongCode(await Service.VerifyAsync(locked, "000000", AccountId, default));
        }

        // The 5th wrong entry cancels the code, and so is every later entry, a right one too: MSG-ACCOUNT-17 (400).
        AssertCancelled(await Service.VerifyAsync(locked, "000000", AccountId, default));
        AssertCancelled(await Service.VerifyAsync(locked, _sms.LastCode, AccountId, default));
        AssertCancelled(await Service.ResendAsync(locked, AccountId, default));
    }

    // BR-ACCOUNT-09: a new code has its own 5 wrong entries; a resend that could not be sent keeps the count of the working code.
    [Fact]
    public async Task A_resent_code_has_its_own_five_wrong_entries_and_a_failed_resend_keeps_the_count()
    {
        var started = await StartAsync();
        for (var i = 0; i < 4; i++)
        {
            AssertWrongCode(await Service.VerifyAsync(started, "000000", AccountId, default));
        }

        _time.Advance(TimeSpan.FromSeconds(60));
        _sms.Fail = true;
        Assert.Equal(ErrorKind.ServiceUnavailable, (await Service.ResendAsync(started, AccountId, default)).Error?.Kind);
        Assert.Equal(4, _codes.All.Single().FailedAttempts);

        _sms.Fail = false;
        Assert.True((await Service.ResendAsync(started, AccountId, default)).IsSuccess);
        for (var i = 0; i < 4; i++)
        {
            AssertWrongCode(await Service.VerifyAsync(started, "000000", AccountId, default));
        }

        Assert.True((await Service.VerifyAsync(started, _sms.LastCode, AccountId, default)).IsSuccess);
    }

    [Fact]
    public async Task Resend_needs_60_seconds_and_stops_after_3_codes_with_the_real_wait()
    {
        var started = await StartAsync();

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, AssertTooManyCodes(await Service.ResendAsync(started, AccountId, default)));

        _time.Advance(TimeSpan.FromSeconds(1));
        var second = await Service.ResendAsync(started, AccountId, default);
        Assert.Equal(1, second.Value?.SendsLeft);

        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, (await Service.ResendAsync(started, AccountId, default)).Value?.SendsLeft);

        // Two minutes after the first code: it leaves the hour 58 minutes from now (not "start again").
        _time.Advance(TimeSpan.FromSeconds(60));
        var fourth = await Service.ResendAsync(started, AccountId, default);
        Assert.Equal(3600 - 180, AssertTooManyCodes(fourth));
        Assert.Equal(0, fourth.Error?.Details?["sendsLeft"]);
        Assert.Equal("You have asked for too many codes. Please try again after 57 minutes.", fourth.Error?.Message.Text);
        Assert.Equal(3, _sms.Sent);
    }

    // BR-ACCOUNT-09 counts codes, not requests: new requests and resends draw on the same 3 codes per hour.
    [Fact]
    public async Task New_requests_and_resends_share_the_3_codes_of_the_hour()
    {
        var first = await StartAsync();
        _time.Advance(TimeSpan.FromSeconds(60));
        await Service.ResendAsync(first, AccountId, default);
        _time.Advance(TimeSpan.FromSeconds(60));
        var second = await StartAsync();
        _time.Advance(TimeSpan.FromSeconds(60));

        var fresh = await Service.StartAsync(VerificationPurpose.VerifyPhone, VerificationChannel.Sms, Phone, AccountId, default);
        var resent = await Service.ResendAsync(second, AccountId, default);

        Assert.Equal(3600 - 180, AssertTooManyCodes(fresh));
        Assert.Equal(3600 - 180, AssertTooManyCodes(resent));
        Assert.Equal(3, _sms.Sent);
    }

    // The hour rolls: each code leaves it on its own, so a resend of the same request works again once the wait is over.
    [Fact]
    public async Task The_retry_time_is_exact_and_the_same_request_can_be_resent_after_it()
    {
        var started = await StartAsync();
        _time.Advance(TimeSpan.FromMinutes(30));
        await Service.ResendAsync(started, AccountId, default);
        _time.Advance(TimeSpan.FromMinutes(29));
        await Service.ResendAsync(started, AccountId, default);
        _time.Advance(TimeSpan.FromSeconds(30));

        var wait = AssertTooManyCodes(await Service.ResendAsync(started, AccountId, default));
        Assert.Equal(30, wait);

        _time.Advance(TimeSpan.FromSeconds(wait - 1));
        Assert.Equal(1, AssertTooManyCodes(await Service.ResendAsync(started, AccountId, default)));

        _time.Advance(TimeSpan.FromSeconds(1));
        var again = await Service.ResendAsync(started, AccountId, default);
        Assert.True(again.IsSuccess, again.Error?.Message.Text);
        Assert.Equal(0, again.Value!.SendsLeft);
        Assert.Equal(4, _sms.Sent);
        Assert.True((await Service.VerifyAsync(started, _sms.LastCode, AccountId, default)).IsSuccess);
    }

    // "One purpose and one phone number or email": whoever asks. Another purpose or target has its own codes.
    [Fact]
    public async Task The_limits_hold_across_accounts_and_not_across_purposes_or_targets()
    {
        await Service.StartAsync(VerificationPurpose.ChangePhone, VerificationChannel.Sms, Phone, Guid.NewGuid(), default);

        var otherAccount = await Service.StartAsync(VerificationPurpose.ChangePhone, VerificationChannel.Sms, Phone, Guid.NewGuid(), default);
        var otherPurpose = await Service.StartAsync(VerificationPurpose.VerifyPhone, VerificationChannel.Sms, Phone, Guid.NewGuid(), default);
        var otherTarget = await Service.StartAsync(VerificationPurpose.ChangePhone, VerificationChannel.Sms, "0987654321", Guid.NewGuid(), default);

        Assert.Equal(60, AssertTooManyCodes(otherAccount));
        Assert.True(otherPurpose.IsSuccess);
        Assert.True(otherTarget.IsSuccess);

        for (var i = 0; i < 2; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(60));
            Assert.True((await Service.StartAsync(VerificationPurpose.ChangePhone, VerificationChannel.Sms, Phone, Guid.NewGuid(), default)).IsSuccess);
        }

        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(3600 - 180, AssertTooManyCodes(await Service.StartAsync(VerificationPurpose.ChangePhone, VerificationChannel.Sms, Phone, Guid.NewGuid(), default)));
    }

    // A failed send changes nothing, so it does not use up a code.
    [Fact]
    public async Task A_code_that_could_not_be_sent_does_not_count()
    {
        _sms.Fail = true;
        for (var i = 0; i < 3; i++)
        {
            await Service.StartAsync(VerificationPurpose.VerifyPhone, VerificationChannel.Sms, Phone, AccountId, default);
            _time.Advance(TimeSpan.FromSeconds(60));
        }

        _sms.Fail = false;
        var started = await Service.StartAsync(VerificationPurpose.VerifyPhone, VerificationChannel.Sms, Phone, AccountId, default);
        Assert.Equal(2, started.Value?.SendsLeft);

        // A resend that fails keeps the earlier code working, and does not use up a code either.
        _time.Advance(TimeSpan.FromSeconds(60));
        var working = _sms.LastCode;
        _sms.Fail = true;
        Assert.Equal(ErrorKind.ServiceUnavailable, (await Service.ResendAsync(started.Value!.VerificationId, AccountId, default)).Error?.Kind);
        _sms.Fail = false;
        Assert.Equal(1, (await Service.ResendAsync(started.Value!.VerificationId, AccountId, default)).Value?.SendsLeft);
        Assert.NotNull(working);
    }

    [Fact]
    public async Task Resent_code_replaces_the_previous_one()
    {
        var started = await StartAsync();
        var firstCode = _sms.LastCode;
        _time.Advance(TimeSpan.FromSeconds(60));
        await Service.ResendAsync(started, AccountId, default);

        if (firstCode != _sms.LastCode)
        {
            AssertWrongCode(await Service.VerifyAsync(started, firstCode, AccountId, default));
        }

        Assert.True((await Service.VerifyAsync(started, _sms.LastCode, AccountId, default)).IsSuccess);
    }

    [Fact]
    public async Task Token_is_bound_to_purpose_account_and_target_and_used_once()
    {
        var started = await StartAsync();
        var token = (await Service.VerifyAsync(started, _sms.LastCode, AccountId, default)).Value!.VerificationToken;

        AssertWrongCode(await Service.ConsumeTokenAsync(token, [VerificationPurpose.ChangePassword], AccountId, null, default));
        AssertWrongCode(await Service.ConsumeTokenAsync(token, [VerificationPurpose.VerifyPhone], Guid.NewGuid(), null, default));
        AssertWrongCode(await Service.ConsumeTokenAsync(token, [VerificationPurpose.VerifyPhone], AccountId, "0987654321", default));
        Assert.True((await Service.ConsumeTokenAsync(token, [VerificationPurpose.VerifyPhone], AccountId, Phone, default)).IsSuccess);
        AssertWrongCode(await Service.ConsumeTokenAsync(token, [VerificationPurpose.VerifyPhone], AccountId, Phone, default));
    }

    [Fact]
    public async Task Token_expires_after_10_minutes()
    {
        var started = await StartAsync();
        var token = (await Service.VerifyAsync(started, _sms.LastCode, AccountId, default)).Value!.VerificationToken;

        _time.Advance(TimeSpan.FromMinutes(10));

        AssertWrongCode(await Service.ConsumeTokenAsync(token, [VerificationPurpose.VerifyPhone], AccountId, null, default));
    }

    [Fact]
    public async Task Request_of_another_account_cannot_be_used()
    {
        var started = await StartAsync();

        AssertWrongCode(await Service.VerifyAsync(started, _sms.LastCode, Guid.NewGuid(), default));
        AssertWrongCode(await Service.ResendAsync(started, null, default));
    }

    [Fact]
    public async Task Sender_failure_saves_nothing_and_answers_MSG_ACCOUNT_13()
    {
        _sms.Fail = true;

        var result = await Service.StartAsync(VerificationPurpose.VerifyPhone, VerificationChannel.Sms, Phone, AccountId, default);

        Assert.Equal(ErrorKind.ServiceUnavailable, result.Error?.Kind);
        Assert.Equal("MSG-ACCOUNT-13", result.Error?.Message.Code);
        Assert.Equal("MSG04", result.Error?.Message.LegacyCode);
        Assert.Equal("The verification code could not be sent by SMS. Nothing has been changed. Please try again later.", result.Error?.Message.Text);
        Assert.Empty(_codes.All);
    }

    [Fact]
    public async Task New_request_cancels_the_open_one_and_codes_are_never_returned()
    {
        var first = await StartAsync();
        _time.Advance(TimeSpan.FromSeconds(61));
        var second = await Service.StartAsync(VerificationPurpose.VerifyPhone, VerificationChannel.Sms, "0987654321", AccountId, default);

        AssertWrongCode(await Service.VerifyAsync(first, _sms.Codes[0], AccountId, default));
        Assert.DoesNotContain(_sms.LastCode, second.Value!.ToString());
        Assert.All(_codes.All, c => Assert.DoesNotContain(_sms.LastCode, c.CodeHash));
    }

    // Switching target or channel does not reset the 60-second spacing of one subject.
    [Fact]
    public async Task Another_target_or_channel_within_60_seconds_is_refused()
    {
        await StartAsync(VerificationPurpose.AccountRecovery);
        _time.Advance(TimeSpan.FromSeconds(30));

        var otherTarget = await Service.StartAsync(VerificationPurpose.AccountRecovery, VerificationChannel.Sms, "0987654321", AccountId, default);
        var otherChannel = await Service.StartAsync(VerificationPurpose.AccountRecovery, VerificationChannel.Email, "a@b.vn", AccountId, default);

        Assert.Equal(ErrorKind.TooManyRequests, otherTarget.Error?.Kind);
        Assert.Equal(ErrorKind.TooManyRequests, otherChannel.Error?.Kind);
    }

    // A guard beyond BR-ACCOUNT-09: an account that tries number after number stops at 5 new requests per hour.
    [Fact]
    public async Task At_most_5_requests_per_hour_for_one_subject_over_its_targets()
    {
        for (var i = 0; i < VerificationService.MaxRequestsPerWindow; i++)
        {
            var started = await Service.StartAsync(VerificationPurpose.ChangePhone, VerificationChannel.Sms, $"091234560{i}", AccountId, default);
            Assert.True(started.IsSuccess);
            _time.Advance(TimeSpan.FromSeconds(61));
        }

        var sixth = await Service.StartAsync(VerificationPurpose.ChangePhone, VerificationChannel.Sms, "0912345609", AccountId, default);
        Assert.Equal(3600 - 5 * 61, AssertTooManyCodes(sixth));

        _time.Advance(VerificationService.BudgetWindow);
        Assert.True((await Service.StartAsync(VerificationPurpose.ChangePhone, VerificationChannel.Sms, "0912345609", AccountId, default)).IsSuccess);
    }

    // The 5th wrong entry cancels one code and nothing more: the next code of the 3 of the hour works as usual.
    [Fact]
    public async Task A_code_cancelled_by_wrong_entries_leaves_the_next_code_working()
    {
        for (var request = 0; request < 2; request++)
        {
            var cancelled = await StartAsync();
            for (var i = 0; i < 4; i++)
            {
                AssertWrongCode(await Service.VerifyAsync(cancelled, "000000", AccountId, default));
            }

            AssertCancelled(await Service.VerifyAsync(cancelled, "000000", AccountId, default));
            _time.Advance(TimeSpan.FromSeconds(60));
        }

        var third = await StartAsync();
        AssertWrongCode(await Service.VerifyAsync(third, "000000", AccountId, default));
        Assert.True((await Service.VerifyAsync(third, _sms.LastCode, AccountId, default)).IsSuccess);
    }

    [Fact]
    public async Task Recovery_requests_cannot_be_resent_here()
    {
        var id = await StartAsync(VerificationPurpose.AccountRecovery);
        _time.Advance(TimeSpan.FromSeconds(61));

        AssertWrongCode(await Service.ResendAsync(id, null, default));
    }

    private async Task<Guid> StartAsync(VerificationPurpose purpose = VerificationPurpose.VerifyPhone)
    {
        var result = await Service.StartAsync(purpose, VerificationChannel.Sms, Phone, AccountId, default);
        Assert.True(result.IsSuccess, result.Error?.Message.Text);
        return result.Value!.VerificationId;
    }

    private static void AssertWrongCode<T>(Result<T> result) => Assert.Equal("MSG-ACCOUNT-16", result.Error?.Message.Code);

    /// <summary>MSG-ACCOUNT-18 (429); returns the wait in seconds, {retry_time} of the text.</summary>
    private static int AssertTooManyCodes<T>(Result<T> result)
    {
        Assert.Equal((ErrorKind.TooManyRequests, "MSG-ACCOUNT-18", "TOO_MANY_REQUESTS"), (result.Error?.Kind, result.Error?.Message.Code, result.Error?.Message.LegacyCode));
        Assert.StartsWith("You have asked for too many codes. Please try again after ", result.Error?.Message.Text);
        return (int)result.Error!.Details!["retryAfterSeconds"]!;
    }

    private static void AssertCancelled<T>(Result<T> result) =>
        Assert.Equal((ErrorKind.Validation, "MSG-ACCOUNT-17", "MSG16"), (result.Error?.Kind, result.Error?.Message.Code, result.Error?.Message.LegacyCode));

    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed partial class FakeSms : ISmsSender
    {
        public bool Fail { get; set; }
        public int Sent { get; private set; }
        public List<string> Codes { get; } = [];
        public string LastCode => Codes[^1];

        public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                return Task.FromResult(DeliveryResult.Failed("DOWN"));
            }

            Sent++;
            Codes.Add(SixDigits().Match(text).Value);
            return Task.FromResult(DeliveryResult.Sent("id"));
        }

        [GeneratedRegex(@"\d{6}")]
        private static partial Regex SixDigits();
    }

    private sealed class NoEmail : IEmailSender
    {
        public Task<DeliveryResult> SendAsync(string to, string subject, string htmlBody, string textBody,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(DeliveryResult.Failed("NOT_USED"));
    }

    private sealed class NoSave : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void DiscardChanges()
        {
        }

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion)
        {
        }

        public void MarkChanged(AuditableEntity entity)
        {
        }
    }

    private sealed class Sha256Hasher : IVerificationCodeHasher
    {
        public string Hash(Guid requestId, string code) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{requestId:N}:{code}")));

        public bool Matches(Guid requestId, string code, string storedHash) => Hash(requestId, code) == storedHash;
    }

    private sealed class FakeCodes : IVerificationCodeRepository
    {
        public List<VerificationCode> All { get; } = [];

        public void Add(VerificationCode code) => All.Add(code);

        public void Remove(VerificationCode code) => All.Remove(code);

        public Task<IReadOnlyList<DateTimeOffset>> GetSendTimesToTargetAsync(string target, DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DateTimeOffset>>(All
                .Where(c => c.Target == target)
                .SelectMany(c => c.SendTimes)
                .Where(sent => sent > since)
                .Order()
                .ToList());

        public Task<int> CountSmsSendsAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult(All.Where(c => c.Channel == VerificationChannel.Sms && c.LastSentAt >= since).Sum(c => c.SendCount));

        public Task<IReadOnlyList<DateTimeOffset>> GetSendTimesAsync(VerificationPurpose purpose, string target, DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DateTimeOffset>>(All
                .Where(c => c.Purpose == purpose && c.Target == target)
                .SelectMany(c => c.SendTimes)
                .Where(sent => sent > since)
                .Order()
                .ToList());

        public Task<VerificationCode?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(All.FirstOrDefault(c => c.Id == id));

        public Task<VerificationCode?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            Task.FromResult(All.FirstOrDefault(c => c.TokenHash == tokenHash));

        public Task<IReadOnlyList<VerificationCode>> GetRecentAsync(
            VerificationPurpose purpose, Guid? accountId, string target, DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VerificationCode>>(All
                .Where(c => c.Purpose == purpose && c.CreatedAt >= since && (accountId != null ? c.AccountId == accountId : c.AccountId == null && c.Target == target))
                .OrderByDescending(c => c.CreatedAt)
                .ToList());

        public Task<IReadOnlyList<VerificationCode>> ListOpenAsync(VerificationPurpose purpose, string target, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VerificationCode>>(All
                .Where(c => c.Purpose == purpose && c.Target == target && c.IsOpen && c.ExpiresAt > now)
                .ToList());

        public Task<IReadOnlyList<VerificationCode>> ListUsableOfAccountAsync(Guid accountId, VerificationChannel channel, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VerificationCode>>(All
                .Where(c => c.AccountId == accountId && c.Channel == channel && c.CancelledAt is null && c.TokenConsumedAt is null)
                .Where(c => c.ExpiresAt > now || c.TokenExpiresAt > now)
                .ToList());
    }
}
