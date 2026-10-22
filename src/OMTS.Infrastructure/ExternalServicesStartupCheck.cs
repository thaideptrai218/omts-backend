using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Infrastructure.Messaging;

namespace OMTS.Infrastructure;

/// <summary>
/// At startup: builds every external adapter so a configuration error (unknown provider, missing Local
/// storage signing key outside Development) stops the host instead of failing the first user request,
/// and warns outside Development that SMS, push or email are only faked.
/// </summary>
internal sealed class ExternalServicesStartupCheck(
    IServiceProvider services,
    IHostEnvironment environment,
    IOptions<SmsOptions> sms,
    IOptions<PushOptions> push,
    IOptions<EmailOptions> email,
    ILogger<ExternalServicesStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        services.GetRequiredService<IEmailSender>();
        services.GetRequiredService<ISmsSender>();
        services.GetRequiredService<IPushSender>();
        services.GetRequiredService<IFileStorage>();

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
