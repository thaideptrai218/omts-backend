using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.WaterQuality;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>Helpers of the QA tests of the mandatory SMS: providers that misbehave in one way each, and a host outside Development.</summary>
public static class MandatorySmsQaKit
{
    public static WaterApiFactory Host(ISmsSender? sms = null, IPushSender? push = null, params (string Key, string Value)[] settings) => new(
        settings.ToDictionary(s => s.Key, s => s.Value),
        services =>
        {
            if (sms is not null)
            {
                services.AddSingleton(sms);
            }

            if (push is not null)
            {
                services.AddSingleton(push);
            }
        });

    /// <summary>Waits for a condition that happens in the background of a request, or gives up after <paramref name="limit"/>.</summary>
    public static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>The API running outside Development, with the settings given.</summary>
    public sealed class ProductionHost(params (string Key, string Value)[] settings) : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }

    /// <summary>Keeps every number it is handed; fails the numbers listed, in the way chosen.</summary>
    public class RecordingSmsSender(Func<string, Task<DeliveryResult>?>? behaviour = null) : ISmsSender
    {
        private readonly List<string> _numbers = [];

        public IReadOnlyList<string> Numbers
        {
            get
            {
                lock (_numbers)
                {
                    return _numbers.ToList();
                }
            }
        }

        public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            var special = behaviour?.Invoke(phoneNumber);
            if (special is not null)
            {
                return special;
            }

            lock (_numbers)
            {
                _numbers.Add(phoneNumber);
            }

            return Task.FromResult(DeliveryResult.Sent("id"));
        }
    }

    /// <summary>Blocks its thread until released: a provider client that is not really asynchronous.</summary>
    public sealed class BlockingSmsSender : ISmsSender, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _release.Set();

        public Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            _release.Wait(TimeSpan.FromSeconds(60), CancellationToken.None);
            return Task.FromResult(DeliveryResult.Sent("id"));
        }

        public void Dispose() => _release.Set();
    }

    /// <summary>Honest but slow: waits for its cancellation and reports what stopped it.</summary>
    public sealed class WaitsForCancellationSmsSender : ISmsSender
    {
        private int _cancelled;

        public int Cancelled => Volatile.Read(ref _cancelled);

        public async Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }

            return DeliveryResult.Sent("id");
        }
    }

    /// <summary>Held at a gate, then delivers: shows what happens around a send in progress.</summary>
    public sealed class GatedSmsSender : ISmsSender
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _delivered;

        public Task Reached => _reached.Task;

        public int Delivered => Volatile.Read(ref _delivered);

        public void Open() => _gate.TrySetResult();

        public async Task<DeliveryResult> SendAsync(string phoneNumber, string text, CancellationToken cancellationToken = default)
        {
            _reached.TrySetResult();
            await _gate.Task;
            Interlocked.Increment(ref _delivered);
            return DeliveryResult.Sent("id");
        }
    }

    /// <summary>A push provider that blocks its thread until released.</summary>
    public sealed class BlockingPushSender : IPushSender, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _release.Set();

        public Task<DeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            _release.Wait(TimeSpan.FromSeconds(60), CancellationToken.None);
            return Task.FromResult(DeliveryResult.Sent("id"));
        }

        public void Dispose() => _release.Set();
    }

    /// <summary>A push provider that never answers and ignores its cancellation, until released.</summary>
    public sealed class HangingPushSender : IPushSender
    {
        private readonly TaskCompletionSource<DeliveryResult> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _answer.TrySetResult(DeliveryResult.Sent("id"));

        public Task<DeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default) => _answer.Task;
    }
}
