using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>Helpers of the tests of the mandatory SMS (decisions 2 and 3 of OMTS-216).</summary>
public static class MandatorySmsKit
{
    /// <summary>What the fake SMS service kept for this number.</summary>
    public static IReadOnlyList<FakeMessage> SmsTo(this WebApplicationFactory<Program> api, string? phone) =>
        api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phone ?? throw new ArgumentNullException(nameof(phone)));

    public static IReadOnlyList<FakeMessage> SmsTo(this WebApplicationFactory<Program> api, Account account) => api.SmsTo(account.PhoneNumber);

    /// <summary>Everything the fake SMS service kept on this host.</summary>
    public static IReadOnlyList<FakeMessage> AllSms(this WebApplicationFactory<Program> api) =>
        api.Services.GetRequiredService<FakeSmsSender>().Inbox.List();

    public static Task<int> NotificationCountAsync(this WebApplicationFactory<Program> api, Guid accountId, string type)
    {
        using var scope = api.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<OmtsDbContext>().Notifications.CountAsync(n => n.RecipientAccountId == accountId && n.Type == type);
    }

    public static AuthApiFactory WithSender(ISmsSender sender) => new WaterQuality.WaterApiFactory(services: services => services.AddSingleton(sender));

    public static AuthApiFactory WithSetting(string key, string value) => new WaterQuality.WaterApiFactory(new Dictionary<string, string> { [key] = value });

    /// <summary>A provider that is down, with an error that repeats what it was given, as some HTTP clients do.</summary>
    public sealed class ThrowingSmsSender : ISmsSender
    {
        public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException($"provider down: {phoneNumber}: {text}");
    }

    /// <summary>A provider that never answers and ignores its cancellation; counts what it was handed.</summary>
    public sealed class HangingSmsSender : ISmsSender
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return new TaskCompletionSource<DeliveryResult>().Task;
        }
    }

    /// <summary>A provider that accepts everything and counts it, to tell "not sent" from "sent and lost".</summary>
    public sealed class CountingSmsSender : ISmsSender
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(DeliveryResult.Sent("id"));
        }
    }
}
