using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Auth;

public sealed class TestRoleTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Tool_is_disabled_by_default_and_anonymous_cannot_use_it()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/me/test-role")).StatusCode);
        var owner = await OwnerAsync();
        var client = await factory.LoginAsync(owner.Username);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/me/test-role")).StatusCode);
    }

    [Fact]
    public async Task Only_configured_linked_owner_can_enter_and_personas_keep_real_account_unchanged()
    {
        var owner = await OwnerAsync();
        await factory.EnsureCooperativeAsync();
        using var host = Enabled(owner.Id);
        var client = await LoginInAsync(host, owner.Username);
        var other = await factory.AddAccountAsync(NewCode(), Password);
        var outsider = await LoginInAsync(host, other.Username);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/me/test-role", new { role = "Owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/me/test-role", new { role = "SystemAdministrator" })).StatusCode);
        var originalPhone = owner.PhoneNumber; var originalZalo = owner.ZaloAccountId;
        foreach (var role in new[] { "Trader", "ZoneManager", "Employee", "Owner" })
        {
            var oldToken = client.DefaultRequestHeaders.Authorization;
            var response = await client.PostAsJsonAsync("/api/me/test-role", new { role });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var tokens = await response.Content.ReadFromJsonAsync<JsonElement>();
            var id = tokens.GetProperty("accountId").GetGuid();
            if (role == "Owner") Assert.Equal(owner.Id, id); else Assert.NotEqual(owner.Id, id);
            var oldClient = host.CreateClient(); oldClient.DefaultRequestHeaders.Authorization = oldToken;
            Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.GetAsync("/api/me/permissions")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
            var access = await client.GetFromJsonAsync<JsonElement>("/api/me/permissions");
            Assert.Equal(role == "Owner", access.GetProperty("isCooperativeOwner").GetBoolean());
            var functions = access.GetProperty("functions").EnumerateArray().ToList();
            if (role == "Employee" || role == "Trader")
            {
                Assert.Empty(functions);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workforce/lookups")).StatusCode);
            }
            if (role == "ZoneManager")
                Assert.Contains(functions, f => f.GetProperty("function").GetString() == "WORK_ASSIGNMENTS");
            if (role is "ZoneManager" or "Employee")
            {
                var day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);
                var personal = await client.GetFromJsonAsync<JsonElement>($"/api/me/workforce?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
                Assert.Equal(JsonValueKind.String, personal.GetProperty("employeeId").ValueKind);
            }
            if (role != "Owner")
            {
                var persona = await factory.QueryAsync(db => db.Accounts.SingleAsync(a => a.Id == id));
                Assert.Null(persona.PhoneNumber); Assert.Null(persona.ZaloAccountId);
                // Existing normal refresh flow keeps the chosen persona, including Employee with no management grants.
                var refreshed = await host.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
                Assert.True(refreshed.IsSuccessStatusCode, await refreshed.Content.ReadAsStringAsync());
                tokens = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(id, tokens.GetProperty("accountId").GetGuid());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
            }
        }
        var real = await factory.QueryAsync(db => db.Accounts.SingleAsync(a => a.Id == owner.Id));
        Assert.Equal(SubjectType.CooperativeOwner, real.SubjectType);
        Assert.Equal(originalPhone, real.PhoneNumber); Assert.Equal(originalZalo, real.ZaloAccountId);
        Assert.False(await factory.QueryAsync(db => db.AccessGrants.AnyAsync(g => g.AccountId == owner.Id)));
        Assert.True(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(a => a.Action == "Development.TestRoleSwitched")));
    }

    [Fact]
    public async Task Disabling_tool_rejects_previously_issued_persona_tokens_and_refresh()
    {
        var owner = await OwnerAsync();
        await factory.EnsureCooperativeAsync();
        using var host = Enabled(owner.Id);
        var client = await LoginInAsync(host, owner.Username);
        var response = await client.PostAsJsonAsync("/api/me/test-role", new { role = "ZoneManager" });
        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>();
        var disabled = factory.CreateClient();
        disabled.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await disabled.GetAsync("/api/me/permissions")).StatusCode);
        Assert.False((await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Production_hides_tool_even_if_configured_enabled()
    {
        var owner = await OwnerAsync();
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Zalo:Provider", "");
            b.UseSetting("Development:TestRoles:Enabled", "true");
            b.UseSetting("Development:TestRoles:OwnerAccountId", owner.Id.ToString());
        });
        var client = await LoginInAsync(host, owner.Username);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/me/test-role")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/me/test-role", new { role = "Trader" })).StatusCode);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Enabled(Guid ownerId) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Development:TestRoles:Enabled", "true");
            b.UseSetting("Development:TestRoles:OwnerAccountId", ownerId.ToString());
        });
    private Task<Account> OwnerAsync() => factory.AddAccountAsync(NewCode(), Password, configure: a =>
    { a.PhoneVerifiedAt = DateTimeOffset.UtcNow; a.ZaloAccountId = NewCode(); });
    private static async Task<HttpClient> LoginInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, string username)
    {
        var client = host.CreateClient();
        var tokens = await LoginTokensAsync(client, username, Password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
