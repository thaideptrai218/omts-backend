using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>Helpers for the account and access-control flows: log in, read codes from the fake inboxes.</summary>
public static partial class AcsTestKit
{
    public const string Password = "Secret123";

    public static string NewPhone() => $"09{Random.Shared.Next(10_000_000, 99_999_999)}";

    public static string NewCode() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>A client carrying the access token of a fresh login.</summary>
    public static async Task<HttpClient> LoginAsync(this AuthApiFactory factory, string identifier, string password = Password)
    {
        var client = factory.CreateClient();
        var tokens = await LoginTokensAsync(client, identifier, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    public static async Task<JsonElement> LoginTokensAsync(HttpClient client, string identifier, string password = Password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier, password });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The code of the latest SMS the fake sender kept for the number.</summary>
    public static string LastSmsCode(this AuthApiFactory factory, string phoneNumber) =>
        CodeIn(factory.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phoneNumber)[0].Text);

    public static string LastEmailCode(this AuthApiFactory factory, string email) =>
        CodeIn(factory.Services.GetRequiredService<FakeEmailSender>().Inbox.List(email)[0].Text);

    /// <summary>Enters the code of a verification request and returns the verification token.</summary>
    public static async Task<string> VerifyAsync(HttpClient client, Guid verificationId, string code)
    {
        var response = await client.PostAsJsonAsync($"/api/verifications/{verificationId}/verify", new { code });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verificationToken").GetString()!;
    }

    public static async Task<Guid> VerificationIdAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var started = body.TryGetProperty("verification", out var nested) ? nested : body;
        return started.GetProperty("verificationId").GetGuid();
    }

    /// <summary>The problem's MSG code; the body is buffered so the test can read it again afterwards.</summary>
    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("code", out var code) ? code.GetString() : $"(no code, HTTP {(int)response.StatusCode})";
    }

    public static async Task<Cooperative> EnsureCooperativeAsync(this AuthApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        if (await db.Cooperatives.FirstOrDefaultAsync() is { } existing)
        {
            return existing;
        }

        var cooperative = new Cooperative { FullName = "HTX Trung Nam" };
        db.Cooperatives.Add(cooperative);
        await db.SaveChangesAsync();
        return cooperative;
    }

    public static async Task<T> QueryAsync<T>(this AuthApiFactory factory, Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    private static string CodeIn(string text) => SixDigits().Match(text).Value;

    [GeneratedRegex(@"\d{6}")]
    private static partial Regex SixDigits();
}
