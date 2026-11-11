using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Domain.UnitTests;

/// <summary>BR-LABOR-02: the rules of the login code, the linked device and the employee app session that need no database.</summary>
public class EmployeeAppEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 3, 0, 0, TimeSpan.Zero);

    private static EmployeeLoginCode NewCode() => new() { IssuedAt = Now, ExpiresAt = Now + EmployeeLoginCode.Lifetime };

    [Fact]
    public void A_login_code_lives_five_minutes_and_allows_five_tries()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), EmployeeLoginCode.Lifetime);
        Assert.Equal(5, EmployeeLoginCode.MaxFailedAttempts);
    }

    [Fact]
    public void A_fresh_code_is_usable_until_it_expires()
    {
        var code = NewCode();

        Assert.True(code.IsUsable(Now));
        Assert.True(code.IsUsable(Now.AddMinutes(4).AddSeconds(59)));
        Assert.False(code.IsUsable(Now.AddMinutes(5)));
        Assert.False(code.IsUsable(Now.AddMinutes(6)));
    }

    [Fact]
    public void The_fifth_wrong_entry_locks_the_code_and_four_do_not()
    {
        var code = NewCode();

        code.FailedAttempts = 4;
        Assert.False(code.IsLocked);
        Assert.True(code.IsUsable(Now));
        code.FailedAttempts = 5;
        Assert.True(code.IsLocked);
        Assert.False(code.IsUsable(Now));
    }

    [Fact]
    public void A_consumed_or_cancelled_code_is_never_usable_again()
    {
        var consumed = NewCode();
        consumed.ConsumedAt = Now;
        var cancelled = NewCode();
        cancelled.CancelledAt = Now;

        Assert.All(new[] { consumed, cancelled }, code =>
        {
            Assert.False(code.IsOpen);
            Assert.False(code.IsUsable(Now));
        });
    }

    [Fact]
    public void Each_code_has_an_id_of_its_own_to_key_its_hash_with()
    {
        Assert.NotEqual(NewCode().Id, NewCode().Id);
        Assert.NotEqual(Guid.Empty, NewCode().Id);
    }

    [Fact]
    public void Unlinking_a_device_keeps_the_first_reason_and_forgets_the_push_token()
    {
        var device = new EmployeeDevice { PushToken = "token", PushPermissionGranted = true, LinkedAt = Now };

        device.Unlink(Now.AddDays(1), EmployeeDeviceUnlinkReason.NewDevice);
        device.Unlink(Now.AddDays(2), EmployeeDeviceUnlinkReason.PhoneChanged);

        Assert.False(device.IsLinked);
        Assert.Equal(Now.AddDays(1), device.UnlinkedAt);
        Assert.Equal(EmployeeDeviceUnlinkReason.NewDevice, device.UnlinkReason);
        Assert.Null(device.PushToken);
        Assert.False(device.PushPermissionGranted);
        Assert.True(new EmployeeDevice().IsLinked);
    }

    [Fact]
    public void Rotating_the_device_secret_keeps_only_the_one_replaced()
    {
        var device = new EmployeeDevice { DeviceSecretHash = "first" };

        device.RotateSecret("second");
        Assert.Equal(("second", "first"), (device.DeviceSecretHash, device.PreviousDeviceSecretHash));

        device.RotateSecret("third");
        Assert.Equal(("third", "second"), (device.DeviceSecretHash, device.PreviousDeviceSecretHash));
    }

    private static EmployeeSession NewSession() => new()
    {
        RefreshTokenHash = "first",
        CreatedAt = Now,
        RefreshTokenExpiresAt = Now.AddDays(30),
        AbsoluteExpiresAt = Now.AddDays(90),
    };

    [Fact]
    public void A_session_refreshes_inside_its_sliding_lifetime_and_is_usable_inside_its_absolute_one()
    {
        var session = NewSession();

        Assert.True(session.CanRefresh(Now.AddDays(29)));
        Assert.False(session.CanRefresh(Now.AddDays(30)));
        Assert.True(session.IsUsable(Now.AddDays(89)));
        Assert.False(session.IsUsable(Now.AddDays(90)));
    }

    [Fact]
    public void A_rotation_replaces_the_hash_slides_the_expiry_but_never_past_the_absolute_limit_and_keeps_the_previous_one_for_the_grace()
    {
        var session = NewSession();
        var grace = TimeSpan.FromSeconds(30);

        session.Rotate("second", Now.AddDays(1), TimeSpan.FromDays(30), grace);

        Assert.Equal(("second", "first"), (session.RefreshTokenHash, session.PreviousRefreshTokenHash));
        Assert.Equal(Now.AddDays(31), session.RefreshTokenExpiresAt);
        Assert.True(session.IsWithinRefreshGrace("first", Now.AddDays(1).AddSeconds(29)));
        Assert.False(session.IsWithinRefreshGrace("first", Now.AddDays(1).AddSeconds(30)));
        Assert.False(session.IsWithinRefreshGrace("second", Now.AddDays(1)));
        Assert.False(session.IsWithinRefreshGrace("other", Now.AddDays(1)));

        session.Rotate("third", Now.AddDays(80), TimeSpan.FromDays(30), grace);

        Assert.Equal(Now.AddDays(90), session.RefreshTokenExpiresAt);
    }

    [Fact]
    public void Revoking_twice_keeps_the_first_reason_and_cause_and_ends_every_use()
    {
        var session = NewSession();

        session.Revoke(Now, "Logout");
        session.Revoke(Now.AddHours(1), "Refresh token reuse detected", SessionRevokeCause.TokenReuse);

        Assert.Equal((Now, "Logout", SessionRevokeCause.Revoked), (session.RevokedAt, session.RevokeReason, session.RevokeCause));
        Assert.False(session.IsUsable(Now));
        Assert.False(session.CanRefresh(Now));
    }
}
