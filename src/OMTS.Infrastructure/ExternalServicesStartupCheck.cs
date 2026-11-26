using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Ocr;
using OMTS.Infrastructure.Zalo;

namespace OMTS.Infrastructure;

/// <summary>
/// At startup: builds every external adapter so a configuration error (unknown provider, missing Local
/// storage signing key, plain-text SMTP outside Development, Zalo Graph without its App secret) stops the host instead of
/// failing the first user request, and warns outside Development that SMS, push, email, OCR, payment notifications or
/// Zalo linking are not really used.
/// </summary>
internal sealed class ExternalServicesStartupCheck(
    IServiceProvider services,
    IHostEnvironment environment,
    IOptions<SmsOptions> sms,
    IOptions<PushOptions> push,
    IOptions<EmailOptions> email,
    IOptions<OcrProviderOptions> ocr,
    IOptions<ZaloOptions> zalo,
    ILogger<ExternalServicesStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        services.GetRequiredService<IEmailSender>();
        services.GetRequiredService<ISmsSender>();
        services.GetRequiredService<IPushSender>();
        services.GetRequiredService<IFileStorage>();
        services.GetRequiredService<IOcrService>();
        LogOcrProviders();

        services.GetRequiredService<IZaloOpenApi>();
        services.GetRequiredService<IPaymentNotificationService>();
        services.GetRequiredService<IZaloOpenApi>();

        if (environment.IsDevelopment())
        {
            return Task.CompletedTask;
        }

        if (ocr.Value.Provider == OcrProviderOptions.FakeProvider)
        {
            // The fake invents readings: outside Development it would put fabricated values into production data.
            throw new InvalidOperationException("Ocr:Provider=Fake is only allowed in Development; leave it unset to read nothing (MSG-WATER-01, manual entry).");
        }

        if (zalo.Value.Provider == ZaloOptions.FakeProvider)
        {
            // The fake trusts made-up tokens: outside Development anyone could link any account by its phone number.
            throw new InvalidOperationException("Zalo:Provider=Fake is only allowed in Development; use Graph with Zalo:AppSecret, or leave it unset.");
        }

        if (email.Value.Provider == EmailOptions.SmtpProvider && email.Value.Smtp.Security == SmtpSecurity.None)
        {
            throw new InvalidOperationException(
                "Email:Smtp:Security 'None' sends credentials in clear text and is allowed in Development only. Use StartTls or SslOnConnect.");
        }

        if (sms.Value.Provider is SmsOptions.FakeProvider or SmsOptions.NoneProvider)
        {
            logger.LogWarning("SMS provider is {Provider}: SMS messages (verification codes, mandatory notifications) are NOT sent, so phone verification cannot complete.", sms.Value.Provider);
        }

        if (push.Value.Provider is PushOptions.FakeProvider or PushOptions.NoneProvider)
        {
            logger.LogWarning("Push provider is {Provider}: push notifications are NOT sent (in-app notifications still are).", push.Value.Provider);
        }

        if (email.Value.Provider == EmailOptions.FakeProvider)
        {
            logger.LogWarning("Email provider is Fake: emails are NOT sent.");
        }

        if (!ocr.Value.ReadsWithModel)
        {
            logger.LogWarning(
                "OCR provider '{Provider}' is not Gemini or Qwen: meter photos, licence maps and raft forms are NOT read; users enter the values.", ocr.Value.Provider);
        }

        if (!string.IsNullOrEmpty(ocr.Value.Fallback) && !ocr.Value.UsesQwenFallback && ocr.Value.Provider != OcrProviderOptions.QwenProvider)
        {
            logger.LogWarning("Ocr:Fallback is '{Fallback}' but the provider is '{Provider}': the fallback is used only behind Gemini.", ocr.Value.Fallback, ocr.Value.Provider);
        }
        logger.LogWarning("Payment notification provider is not configured: no receiving bank account can be accepted (MSG-DEBT-02), so VietQR stays out of use.");

        if (zalo.Value.Provider != ZaloOptions.GraphProvider)
        {
            logger.LogWarning("Zalo provider is not configured: the Zalo Mini App cannot link accounts (MSG04).");
        }

        return Task.CompletedTask;
    }

    private void LogOcrProviders()
    {
        var value = ocr.Value;
        if (value.Provider == OcrProviderOptions.GeminiProvider)
        {
            var readers = value.Gemini.Readers;
            logger.LogInformation(
                "OCR provider Gemini: meter {Meter} ({MeterTimeout} s), licence map {ZoneMap} ({ZoneMapTimeout} s), raft form {RaftForm} ({RaftFormTimeout} s)",
                readers.Meter.Model, readers.Meter.TimeoutSeconds, readers.ZoneMap.Model, readers.ZoneMap.TimeoutSeconds, readers.RaftForm.Model, readers.RaftForm.TimeoutSeconds);
        }

        if (value.Provider == OcrProviderOptions.QwenProvider || value.UsesQwenFallback)
        {
            var readers = value.Qwen.Readers;
            logger.LogInformation(
                "OCR provider {Role} Qwen: meter {Meter} ({MeterTimeout} s), licence map {ZoneMap} ({ZoneMapTimeout} s), raft form {RaftForm} ({RaftFormTimeout} s), thinking {Thinking}, scan budget {Budget} s",
                value.Provider == OcrProviderOptions.QwenProvider ? "primary" : "fallback",
                readers.Meter.Model, readers.Meter.TimeoutSeconds, readers.ZoneMap.Model, readers.ZoneMap.TimeoutSeconds, readers.RaftForm.Model, readers.RaftForm.TimeoutSeconds,
                value.Qwen.DisableThinking ? "off" : "model default", value.ReadBudgetSeconds);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
