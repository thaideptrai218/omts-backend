using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OMTS.Infrastructure.Messaging.Fakes;

/// <summary>Warns at startup outside Development that SMS, push or email are not really sent.</summary>
internal sealed class FakeSenderStartupWarning(
    IHostEnvironment environment,
    IOptions<SmsOptions> sms,
    IOptions<PushOptions> push,
    IOptions<EmailOptions> email,
    ILogger<FakeSenderStartupWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return Task.CompletedTask;
        }

        if (sms.Value.Provider == SmsOptions.FakeProvider)
        {
            logger.LogWarning("SMS provider is Fake: SMS messages (verification codes) are NOT sent.");
        }

        if (push.Value.Provider == PushOptions.FakeProvider)
        {
            logger.LogWarning("Push provider is Fake: push notifications are NOT sent.");
        }

        if (email.Value.Provider == EmailOptions.FakeProvider)
        {
            logger.LogWarning("Email provider is Fake: emails are NOT sent.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
