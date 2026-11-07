using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>Helpers of the QA tests of the account rules (OMTS-216).</summary>
internal static class AccountRulesQaTestsKit
{
    public static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

    /// <summary>A code that is certainly not <paramref name="code"/>; generated, so no code-like literal is committed.</summary>
    public static string WrongCodeFor(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    public static Task<HttpResponseMessage> EnterAsync(this HttpClient client, Guid verificationId, string code) =>
        client.PostAsJsonAsync($"/api/verifications/{verificationId}/verify", new { code });

    public static Task<HttpResponseMessage> StartRegistrationAsync(this HttpClient client, string channel, string target) =>
        client.PostAsJsonAsync("/api/verifications", new { channel, target });

    public static Task AgeAsync(this AuthApiFactory factory, Guid verificationId, TimeSpan age) =>
        factory.UpdateAsync<VerificationCode>(verificationId, c => c.AgeBy(age));

    public static async Task<JsonElement> BodyAsync(this HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>Takes the account's phone step, so that only the temporary password is left.</summary>
    public static async Task VerifyPhoneAsync(this AuthApiFactory factory, HttpClient client, string phone)
    {
        var id = await VerificationIdAsync(await client.PostAsJsonAsync("/api/me/phone-verification", new { phoneNumber = phone }));
        var token = await VerifyAsync(client, id, factory.LastSmsCode(phone));
        var confirmed = await client.PostAsJsonAsync("/api/me/phone-verification/confirm", new { verificationToken = token });
        Assert.True(confirmed.IsSuccessStatusCode, await confirmed.Content.ReadAsStringAsync());
    }

    /// <summary>An SMS gateway as slow as a real one, to see whether an answer waits for it.</summary>
    public sealed class SlowSmsSender(TimeSpan delay) : ISmsSender
    {
        public async Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return DeliveryResult.Sent(Guid.NewGuid().ToString("N"));
        }
    }
}
