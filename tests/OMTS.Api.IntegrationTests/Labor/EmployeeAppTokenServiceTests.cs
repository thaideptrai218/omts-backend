using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Identity;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>The employee app token (BR-LABOR-02): its claims, its audience, its own signing key, its lifetimes and the QR key, apart from a staff token.</summary>
public class EmployeeAppTokenServiceTests
{
    private static JwtOptions NewOptions() => new() { SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) };

    private static JwtTokenService Service(JwtOptions options) => new(Options.Create(options), TimeProvider.System);

    private static TokenValidationParameters Parameters(JwtOptions options, string audience, SecurityKey key) => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = audience,
        IssuerSigningKey = key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.Zero,
    };

    private static readonly Employee Worker = new() { FullName = "Nguyen Van A" };

    [Fact]
    public void An_employee_token_names_the_employee_the_session_and_the_device_and_no_account()
    {
        var options = NewOptions();
        var (session, device) = (Guid.NewGuid(), Guid.NewGuid());

        var token = Service(options).CreateEmployeeAccessToken(Worker, session, device, DateTimeOffset.UtcNow.AddDays(30)).Token;

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token);
        Assert.Equal(Worker.Id.ToString(), jwt.GetClaim(OmtsClaimTypes.EmployeeId).Value);
        Assert.Equal(session.ToString(), jwt.GetClaim(OmtsClaimTypes.EmployeeSessionId).Value);
        Assert.Equal(device.ToString(), jwt.GetClaim(OmtsClaimTypes.EmployeeDeviceId).Value);
        Assert.Equal("Employee", jwt.GetClaim(OmtsClaimTypes.SubjectType).Value);
        Assert.Equal(Worker.FullName, jwt.GetClaim(OmtsClaimTypes.Name).Value);
        Assert.False(jwt.TryGetClaim(OmtsClaimTypes.Subject, out _));
        Assert.False(jwt.TryGetClaim(OmtsClaimTypes.SessionId, out _));
        Assert.Equal([options.EmployeeAudience], jwt.Audiences);
        Assert.Equal(options.Issuer, jwt.Issuer);
    }

    [Fact]
    public async Task Each_kind_of_token_passes_only_its_own_audience_and_key()
    {
        var options = NewOptions();
        var service = Service(options);
        var employee = service.CreateEmployeeAccessToken(Worker, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)).Token;
        var staff = service.CreateAccessToken(
            new Account { Username = "staff", SubjectType = SubjectType.Staff }, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)).Token;
        var (staffKey, employeeKey) = (options.CreateSigningKey(), options.CreateEmployeeSigningKey());
        var handler = new JsonWebTokenHandler();

        Assert.True((await handler.ValidateTokenAsync(employee, Parameters(options, options.EmployeeAudience, employeeKey))).IsValid);
        Assert.True((await handler.ValidateTokenAsync(staff, Parameters(options, options.Audience, staffKey))).IsValid);
        // Neither the audience nor the key of the other kind, and no mix of the two, lets a token through.
        foreach (var (token, audience, key) in new[]
        {
            (employee, options.Audience, staffKey), (employee, options.Audience, employeeKey), (employee, options.EmployeeAudience, staffKey),
            (staff, options.EmployeeAudience, employeeKey), (staff, options.EmployeeAudience, staffKey), (staff, options.Audience, employeeKey),
        })
        {
            Assert.False((await handler.ValidateTokenAsync(token, Parameters(options, audience, key))).IsValid);
        }
    }

    [Fact]
    public void The_employee_signing_key_is_derived_not_the_staff_key_and_the_same_every_time()
    {
        var options = NewOptions();

        Assert.NotEqual(options.CreateSigningKey().Key, options.CreateEmployeeSigningKey().Key);
        Assert.Equal(options.CreateEmployeeSigningKey().Key, options.CreateEmployeeSigningKey().Key);
        Assert.NotEqual(options.CreateEmployeeSigningKey().Key, NewOptions().CreateEmployeeSigningKey().Key);
        Assert.Equal(32, options.CreateEmployeeSigningKey().Key.Length);
    }

    [Fact]
    public void An_employee_token_never_outlives_its_session_and_the_session_lives_as_a_mobile_one()
    {
        var options = NewOptions();
        var service = Service(options);
        var soon = DateTimeOffset.UtcNow.AddMinutes(2);

        var access = service.CreateEmployeeAccessToken(Worker, Guid.NewGuid(), Guid.NewGuid(), soon);
        var normal = service.CreateEmployeeAccessToken(Worker, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(10));

        Assert.Equal(soon, access.ExpiresAt);
        Assert.InRange(normal.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        var lifetime = service.GetEmployeeSessionLifetime();
        Assert.Equal((TimeSpan.FromDays(30), TimeSpan.FromDays(90), TimeSpan.FromSeconds(30)), (lifetime.RefreshTokenLifetime, lifetime.MaxLifetime, lifetime.RefreshGrace));
        Assert.Equal(service.GetSessionLifetime(ClientType.Mobile), lifetime);
    }

    // LABOR-07: the app signs the QR code with this key, the server checks it with the same one, so it is a function of the device alone.
    [Fact]
    public void The_qr_key_is_one_per_device_derived_from_the_signing_key_and_never_another_key()
    {
        var options = NewOptions();
        var service = Service(options);
        var device = Guid.NewGuid();

        var key = service.DeriveEmployeeQrKey(device);

        Assert.Equal(43, key.Length);
        Assert.Equal(key, service.DeriveEmployeeQrKey(device));
        Assert.NotEqual(key, service.DeriveEmployeeQrKey(Guid.NewGuid()));
        Assert.NotEqual(key, Service(NewOptions()).DeriveEmployeeQrKey(device));
        Assert.NotEqual(key, service.DeriveDeviceKey(device.ToString("N")));
        Assert.NotEqual(key, Convert.ToBase64String(options.CreateSigningKey().Key));
    }

    // The host does not start when the two kinds of token could share an audience.
    [Fact]
    public void The_host_refuses_to_start_when_the_two_audiences_are_the_same_or_empty()
    {
        foreach (var settings in new[] { ("Jwt:EmployeeAudience", "omts-clients"), ("Jwt:EmployeeAudience", " ") })
        {
            using var baseApi = new AuthApiFactory();
            using var api = baseApi.WithWebHostBuilder(b => b.UseSetting(settings.Item1, settings.Item2));

            var error = Record.Exception(() => api.Services);

            var validation = Assert.IsType<OptionsValidationException>(error?.GetBaseException());
            Assert.Contains("EmployeeAudience", string.Join(' ', validation.Failures));
        }
    }
}
