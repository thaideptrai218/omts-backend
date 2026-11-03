using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Ocr;

namespace OMTS.Infrastructure;

/// <summary>
/// At startup: builds every external adapter so a configuration error (unknown provider, missing Local
/// storage signing key, plain-text SMTP outside Development) stops the host instead of failing the first
/// user request, and warns outside Development that SMS, push, email or OCR are not really used.
/// </summary>
internal sealed class ExternalServicesStartupCheck(
    IServiceProvider services,
    IHostEnvironment environment,
    IOptions<SmsOptions> sms,
    IOptions<PushOptions> push,
    IOptions<EmailOptions> email,
    IOptions<OcrProviderOptions> ocr,
    ILogger<ExternalServicesStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        services.GetRequiredService<IEmailSender>();
        services.GetRequiredService<ISmsSender>();
        services.GetRequiredService<IPushSender>();
        services.GetRequiredService<IFileStorage>();
        services.GetRequiredService<IOcrService>();

        if (environment.IsDevelopment())
        {
            return Task.CompletedTask;
        }

        if (ocr.Value.Provider == OcrProviderOptions.FakeProvider)
        {
            // The fake invents readings: outside Development it would put fabricated values into production data.
            throw new InvalidOperationException("Ocr:Provider=Fake is only allowed in Development; leave it unset to read nothing (MSG-WATER-01, manual entry).");
        }

        if (email.Value.Provider == EmailOptions.SmtpProvider && email.Value.Smtp.Security == SmtpSecurity.None)
        {
            throw new InvalidOperationException(
                "Email:Smtp:Security 'None' sends credentials in clear text and is allowed in Development only. Use StartTls or SslOnConnect.");
        }

        if (sms.Value.Provider is SmsOptions.FakeProvider or SmsOptions.NoneProvider)
        {
            logger.LogWarning("SMS provider is {Provider}: SMS messages (verification codes) are NOT sent, so phone verification cannot complete.", sms.Value.Provider);
        }

        if (push.Value.Provider is PushOptions.FakeProvider or PushOptions.NoneProvider)
        {
            logger.LogWarning("Push provider is {Provider}: push notifications are NOT sent (in-app notifications still are).", push.Value.Provider);
        }

        if (email.Value.Provider == EmailOptions.FakeProvider)
        {
            logger.LogWarning("Email provider is Fake: emails are NOT sent.");
        }

        logger.LogWarning("OCR provider '{Provider}' is not configured: meter photos are NOT read; users enter the values.", ocr.Value.Provider);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
