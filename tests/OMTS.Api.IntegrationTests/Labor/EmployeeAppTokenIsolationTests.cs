using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Api.Controllers;
using OMTS.Api.Extensions;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Infrastructure.Identity;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.EmployeeAppKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// BR-LABOR-02: the employee app is a second kind of caller. An employee token is refused by every staff endpoint and a staff token by
/// every employee app endpoint (401), and a token forged with the other kind's key, audience or claims is refused by both.
/// </summary>
public partial class EmployeeAppTokenIsolationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private IEnumerable<ActionDescriptor> Actions => factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;

    private static bool NamesEmployeeScheme(ActionDescriptor action) =>
        action.EndpointMetadata.OfType<IAuthorizeData>().Any(a => a.AuthenticationSchemes == EmployeeAppAuthentication.Scheme);

    private static string Url(string template) =>
        RouteParameter().Replace(template, match => match.Value.Contains(":int") || match.Value.Contains(":long") ? "1" : Guid.NewGuid().ToString());

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();

    /// <summary>Calls the actions picked by <paramref name="pick"/> with the client; returns those that do not answer 401.</summary>
    private async Task<(List<string> Wrong, int Called)> SweepAsync(HttpClient client, Func<ActionDescriptor, bool> pick)
    {
        var (wrong, called) = (new List<string>(), 0);
        foreach (var action in Actions.Where(pick))
        {
            var template = action.AttributeRouteInfo?.Template;
            var methods = action.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(c => c.HttpMethods).ToList();
            if (template is null || methods is null || template.StartsWith("test-only/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var method in methods)
            {
                called++;
                var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/" + Url(template)));
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    wrong.Add($"{method} {template}: {(int)response.StatusCode}");
                }
            }
        }

        return (wrong, called);
    }

    [Fact]
    public async Task An_employee_token_is_refused_by_every_staff_endpoint()
    {
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        var phone = factory.AsEmployee(linked);
        // The token is good where it belongs.
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PutAsJsonAsync(PushToken, new { pushToken = (string?)null, pushPermissionGranted = false })).StatusCode);

        var (wrong, called) = await SweepAsync(phone, a => a.EndpointMetadata.OfType<IAuthorizeData>().Any()
            && !a.EndpointMetadata.OfType<IAllowAnonymous>().Any() && !NamesEmployeeScheme(a));

        Assert.True(called > 100, $"Only {called} endpoints were called.");
        Assert.Empty(wrong);
        // The ones a person reads first, by name.
        foreach (var path in new[] { "/api/me", "/api/me/permissions", "/api/employees", "/api/accounts", "/api/audit-events" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task A_staff_token_is_refused_by_every_employee_app_endpoint()
    {
        var world = await WorldAsync(factory);

        foreach (var staff in new[] { world.Owner, world.Manager })
        {
            var (wrong, called) = await SweepAsync(staff, a => NamesEmployeeScheme(a) && !a.EndpointMetadata.OfType<IAllowAnonymous>().Any());

            Assert.True(called >= 2, $"Only {called} employee app endpoints were called.");
            Assert.Empty(wrong);
        }
    }

    // The employee scheme is opt-in: the default one stays the staff scheme, and only the employee app controller names the other.
    [Fact]
    public async Task The_default_scheme_is_the_staff_one_and_only_the_employee_app_controller_names_the_employee_scheme()
    {
        var defaultScheme = await factory.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();

        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, defaultScheme?.Name);
        var named = Actions.Where(NamesEmployeeScheme).ToList();
        Assert.True(named.Count >= 4);
        Assert.All(named, a => Assert.Equal(typeof(EmployeeAppController), (a as ControllerActionDescriptor)?.ControllerTypeInfo.AsType()));
        var login = Actions.Single(a => a.AttributeRouteInfo?.Template == "api/employee-app/login");
        Assert.Contains(login.EndpointMetadata, m => m is IAllowAnonymous);
    }

    [Fact]
    public async Task A_token_forged_with_the_other_kinds_key_audience_or_claims_is_refused_by_both()
    {
        var jwt = factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var world = await WorldAsync(factory);
        var worker = await AddWorkerAsync(world);
        var linked = await LinkDeviceAsync(factory, world, worker);
        var other = await AddWorkerAsync(world);
        var otherLinked = await LinkDeviceAsync(factory, world, other);
        var session = Assert.Single(await factory.SessionsAsync(worker.Id));
        var ownerSession = await factory.QueryAsync(db => db.LoginSessions.AsNoTracking().Where(s => s.AccountId == world.OwnerAccount.Id).SingleAsync());
        var employeeClaims = EmployeeClaims(worker.Id, session.Id, linked.GetProperty("deviceId").GetGuid());
        var staffClaims = new Dictionary<string, object>
        {
            [OmtsClaimTypes.Subject] = world.OwnerAccount.Id.ToString(),
            [OmtsClaimTypes.SessionId] = ownerSession.Id.ToString(),
            [OmtsClaimTypes.TokenId] = Guid.NewGuid().ToString(),
            [OmtsClaimTypes.Name] = world.OwnerAccount.Username,
            [OmtsClaimTypes.SubjectType] = world.OwnerAccount.SubjectType.ToString(),
        };
        var (staffKey, employeeKey) = (jwt.CreateSigningKey(), jwt.CreateEmployeeSigningKey());

        // Control: a token made here the way the server makes one is accepted, so each refusal below is the forgery's doing.
        await AssertAcceptedByEmployeeAppAsync(Forge(employeeKey, jwt.EmployeeAudience, jwt.Issuer, employeeClaims));
        await AssertAcceptedByStaffAsync(Forge(staffKey, jwt.Audience, jwt.Issuer, staffClaims));

        var forged = new (string Name, string Token)[]
        {
            ("employee claims, employee audience, the staff key", Forge(staffKey, jwt.EmployeeAudience, jwt.Issuer, employeeClaims)),
            ("employee claims, staff audience, the employee key", Forge(employeeKey, jwt.Audience, jwt.Issuer, employeeClaims)),
            ("employee claims, staff audience, the staff key", Forge(staffKey, jwt.Audience, jwt.Issuer, employeeClaims)),
            ("employee claims, other issuer", Forge(employeeKey, jwt.EmployeeAudience, "evil-issuer", employeeClaims)),
            ("staff claims, employee audience, the employee key", Forge(employeeKey, jwt.EmployeeAudience, jwt.Issuer, staffClaims)),
            ("staff claims, employee audience, the staff key", Forge(staffKey, jwt.EmployeeAudience, jwt.Issuer, staffClaims)),
            ("staff claims, staff audience, the employee key", Forge(employeeKey, jwt.Audience, jwt.Issuer, staffClaims)),
            ("employee and staff claims together", Forge(employeeKey, jwt.EmployeeAudience, jwt.Issuer, employeeClaims.Concat(staffClaims.Where(c => c.Key is OmtsClaimTypes.Subject or OmtsClaimTypes.SessionId)).ToDictionary())),
            ("a session nobody opened", Forge(employeeKey, jwt.EmployeeAudience, jwt.Issuer, EmployeeClaims(worker.Id, Guid.NewGuid(), linked.GetProperty("deviceId").GetGuid()))),
            ("another employee's device", Forge(employeeKey, jwt.EmployeeAudience, jwt.Issuer, EmployeeClaims(worker.Id, session.Id, otherLinked.GetProperty("deviceId").GetGuid()))),
            ("another employee's id", Forge(employeeKey, jwt.EmployeeAudience, jwt.Issuer, EmployeeClaims(other.Id, session.Id, linked.GetProperty("deviceId").GetGuid()))),
            ("an expired employee token", Forge(employeeKey, jwt.EmployeeAudience, jwt.Issuer, employeeClaims, expires: DateTime.UtcNow.AddMinutes(-5))),
            ("an unsigned employee token", Unsigned(jwt.EmployeeAudience, jwt.Issuer, employeeClaims)),
            ("an unsigned staff token", Unsigned(jwt.Audience, jwt.Issuer, staffClaims)),
        };

        foreach (var (name, token) in forged)
        {
            var client = WithBearer(factory.CreateClient(), token);
            Assert.True(HttpStatusCode.Unauthorized == (await client.PostAsync(Logout, null)).StatusCode, $"{name}: the employee app accepted it.");
            Assert.True(HttpStatusCode.Unauthorized == (await client.GetAsync("/api/me")).StatusCode, $"{name}: a staff endpoint accepted it.");
        }
    }

    private async Task AssertAcceptedByEmployeeAppAsync(string token) =>
        Assert.Equal(HttpStatusCode.NoContent, (await WithBearer(factory.CreateClient(), token).PutAsJsonAsync(PushToken, new { pushToken = (string?)null, pushPermissionGranted = false })).StatusCode);

    private async Task AssertAcceptedByStaffAsync(string token) =>
        Assert.Equal(HttpStatusCode.OK, (await WithBearer(factory.CreateClient(), token).GetAsync("/api/me")).StatusCode);

    private static Dictionary<string, object> EmployeeClaims(Guid employeeId, Guid sessionId, Guid deviceId) => new()
    {
        [OmtsClaimTypes.EmployeeId] = employeeId.ToString(),
        [OmtsClaimTypes.EmployeeSessionId] = sessionId.ToString(),
        [OmtsClaimTypes.EmployeeDeviceId] = deviceId.ToString(),
        [OmtsClaimTypes.TokenId] = Guid.NewGuid().ToString(),
        [OmtsClaimTypes.Name] = "Forged",
        [OmtsClaimTypes.SubjectType] = OmtsClaimTypes.EmployeeSubjectType,
    };

    private static string Forge(Microsoft.IdentityModel.Tokens.SecurityKey key, string audience, string issuer, IDictionary<string, object> claims, DateTime? expires = null) =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
            Claims = claims,
        });

    /// <summary>A token whose header says <c>alg: none</c>: no signature to check.</summary>
    private static string Unsigned(string audience, string issuer, IDictionary<string, object> claims)
    {
        string Part(object value) => Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(JsonSerializer.Serialize(value));
        var payload = new Dictionary<string, object>(claims)
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
        };
        return $"{Part(new { alg = "none", typ = "JWT" })}.{Part(payload)}.";
    }
}
