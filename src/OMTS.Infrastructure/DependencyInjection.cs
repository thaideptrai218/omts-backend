using OMTS.Infrastructure.Persistence.Repositories.Farming;
using OMTS.Infrastructure.Persistence.Repositories.Identity;
using OMTS.Infrastructure.Persistence.Repositories.Organization;
using OMTS.Infrastructure.Persistence.Repositories.Platform;
using OMTS.Infrastructure.Persistence.Repositories.Purchasing;
using OMTS.Infrastructure.Persistence.Repositories.Sales;
using OMTS.Infrastructure.Persistence.Repositories.WaterQuality;
using OMTS.Infrastructure.Persistence.Repositories.Workforce;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Verification;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.HealthChecks;
using OMTS.Infrastructure.Identity;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Ocr;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Interceptors;
using OMTS.Infrastructure.Persistence.Queries;
using OMTS.Infrastructure.Persistence.Repositories;
using OMTS.Infrastructure.Persistence.Seed;
using OMTS.Infrastructure.Reports;
using OMTS.Infrastructure.Storage;
using OMTS.Infrastructure.Zalo;

namespace OMTS.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "OmtsDb";

    /// <summary>Registers persistence and external adapters.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<PermissionCacheInvalidationInterceptor>();
        // Scoped (it reads the current user): works because AddDbContext builds the options per scope.
        // Switching to DbContext pooling or a context factory would need another way to pass the user.
        services.AddScoped<AuditableEntityStampInterceptor>();
        services.AddScoped<PushOutboxInterceptor>();
        services.AddDbContext<OmtsDbContext>((provider, options) =>
        {
            options.AddOmtsInterceptors(provider);

            // Resolved lazily so the API can start (e.g. /health, tests) before a database is configured.
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not set. " +
                    $"Run: dotnet user-secrets set \"ConnectionStrings:{ConnectionStringName}\" \"<value>\" --project src/OMTS.Api");
            }

            options.UseSqlServer(connectionString);
        });
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ILoginSessionRepository, LoginSessionRepository>();
        services.AddScoped<IAccessGrantRepository, AccessGrantRepository>();
        services.AddScoped<IFarmingZoneRepository, FarmingZoneRepository>();
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IZoneRecipientQuery, ZoneRecipientQuery>();
        services.AddScoped<IVerificationCodeRepository, VerificationCodeRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ICooperativeRepository, CooperativeRepository>();
        services.AddScoped<IPushDeviceRepository, PushDeviceRepository>();
        services.AddScoped<IEmployeeRepository, EmployeeRepository>();
        services.AddScoped<IPurchaseRequestRepository, PurchaseRequestRepository>();
        services.AddScoped<ISalesProductRepository, SalesProductRepository>();
        services.AddScoped<IWorkforceRepository, WorkforceRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IRaftRepository, RaftRepository>();
        services.AddScoped<IZoneLicenseRepository, ZoneLicenseRepository>();
        services.AddScoped<IRaftMaterialCostQuery, RaftMaterialCostQuery>();
        services.AddScoped<IStoredFileReferenceQuery, StoredFileReferenceQuery>();
        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<IFarmingInputItemRepository, FarmingInputItemRepository>();
        services.AddScoped<IPurchaseReceiptRepository, PurchaseReceiptRepository>();
        services.AddScoped<ISupplierPaymentRepository, SupplierPaymentRepository>();
        services.AddScoped<IWaterMeasurementRepository, WaterMeasurementRepository>();
        services.AddSingleton<IStatusCatalog, StatusCatalog>();

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IVerificationCodeHasher, HmacVerificationCodeHasher>();
        services.AddOptions<VerificationLimitOptions>().Bind(configuration.GetSection(VerificationLimitOptions.SectionName));
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        services.AddMessaging(configuration);
        services.AddFileStorage(configuration);
        services.AddOcr(configuration);
        services.AddZalo(configuration);
        services.AddSingleton<ISpreadsheetWriter, ClosedXmlSpreadsheetWriter>();

        services.AddScoped<DeploymentBootstrapper>();
        services.AddScoped<OwnerPasswordReset>();

        // FARM-03 expiring zone license reminder (formerly JOB-06)
        services.AddOptions<ZoneLicenseReminderOptions>().Bind(configuration.GetSection(ZoneLicenseReminderOptions.SectionName));
        services.AddSingleton<ZoneLicenseReminderJob>();
        services.AddHostedService<ZoneLicenseReminderHostedService>();

        // Overdue water measurement reminder (formerly JOB-05)
        services.AddOptions<WaterMeasurementOverdueOptions>().Bind(configuration.GetSection(WaterMeasurementOverdueOptions.SectionName));
        services.AddHostedService<WaterMeasurementOverdueJob>();

        // Raft photo and license files no record points to (cooperative logos are never touched)
        services.AddOptions<OrphanFileCleanupOptions>()
            .Bind(configuration.GetSection(OrphanFileCleanupOptions.SectionName))
            .Validate(o => o.GracePeriod >= OrphanFileCleanupOptions.MinimumGracePeriod, $"{OrphanFileCleanupOptions.SectionName}:GracePeriod must be at least 01:00:00.")
            .ValidateOnStart();
        services.AddSingleton<OrphanFileCleanupJob>();
        services.AddHostedService<OrphanFileCleanupHostedService>();
        services.AddHealthChecks().AddReadinessChecks();

        return services;
    }

    /// <summary>
    /// Interceptors every <see cref="OmtsDbContext"/> needs (audit stamps, permission cache invalidation,
    /// push after commit); also used by tests that swap the database provider.
    /// </summary>
    public static DbContextOptionsBuilder AddOmtsInterceptors(this DbContextOptionsBuilder options, IServiceProvider provider) =>
        options.AddInterceptors(
            provider.GetRequiredService<AuditableEntityStampInterceptor>(),
            provider.GetRequiredService<PermissionCacheInvalidationInterceptor>(),
            provider.GetRequiredService<PushOutboxInterceptor>());

    // Providers are picked when first resolved, from the final configuration (tests and environments
    // override it after this method runs).
    private static void AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.SectionName));
        services.AddOptions<SmsOptions>().Bind(configuration.GetSection(SmsOptions.SectionName));
        services.AddOptions<PushOptions>().Bind(configuration.GetSection(PushOptions.SectionName));

        services.AddSingleton<FakeEmailSender>();
        services.AddSingleton<FakeSmsSender>();
        services.AddSingleton<FakePushSender>();
        services.AddSingleton<SmtpEmailSender>();

        services.AddSingleton<IEmailSender>(provider => provider.GetRequiredService<IOptions<EmailOptions>>().Value.Provider switch
        {
            EmailOptions.FakeProvider => provider.GetRequiredService<FakeEmailSender>(),
            EmailOptions.SmtpProvider => provider.GetRequiredService<SmtpEmailSender>(),
            var other => throw UnknownProvider(EmailOptions.SectionName, other),
        });
        // Only fakes exist for SMS and push until their provider tickets. The fake is configured for Development
        // only; elsewhere the default "None" fails every send, so nothing pretends a code or alert was delivered.
        services.AddSingleton<ISmsSender>(provider => provider.GetRequiredService<IOptions<SmsOptions>>().Value.Provider switch
        {
            SmsOptions.FakeProvider => provider.GetRequiredService<FakeSmsSender>(),
            SmsOptions.NoneProvider => ActivatorUtilities.CreateInstance<UnconfiguredSmsSender>(provider),
            var other => throw UnknownProvider(SmsOptions.SectionName, other),
        });
        services.AddSingleton<IPushSender>(provider => provider.GetRequiredService<IOptions<PushOptions>>().Value.Provider switch
        {
            PushOptions.FakeProvider => provider.GetRequiredService<FakePushSender>(),
            PushOptions.NoneProvider => ActivatorUtilities.CreateInstance<UnconfiguredPushSender>(provider),
            var other => throw UnknownProvider(PushOptions.SectionName, other),
        });

        services.AddScoped<PushOutbox>();
        services.AddScoped<IPushOutbox>(provider => provider.GetRequiredService<PushOutbox>());

        // Resolves the configured adapters at startup, so a configuration error stops the host right away.
        services.AddHostedService<ExternalServicesStartupCheck>();
    }

    private static void AddFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName));
        services.AddSingleton<LocalFileUrlSigner>();
        services.AddSingleton<LocalFileStorage>();
        services.AddSingleton<AzureBlobFileStorage>();
        services.AddSingleton<IFileStorage>(provider => provider.GetRequiredService<IOptions<StorageOptions>>().Value.Provider switch
        {
            StorageOptions.LocalProvider => provider.GetRequiredService<LocalFileStorage>(),
            StorageOptions.AzureBlobProvider => provider.GetRequiredService<AzureBlobFileStorage>(),
            var other => throw UnknownProvider(StorageOptions.SectionName, other),
        });
    }

    private static void AddOcr(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OcrOptions>().Bind(configuration.GetSection(OcrOptions.SectionName));
        services.AddOptions<OcrProviderOptions>().Bind(configuration.GetSection(OcrProviderOptions.SectionName));
        services.AddSingleton<FakeOcrService>();
        services.AddSingleton<NotConfiguredOcrService>();
        // Only the fake (Development/tests) exists until the real OCR provider ticket. Unset or unknown fails
        // closed: no readings, MSG-WATER-01 and manual entry, instead of fabricated values.
        services.AddSingleton<IOcrService>(provider =>
        {
            var name = provider.GetRequiredService<IOptions<OcrProviderOptions>>().Value.Provider;
            return name == OcrProviderOptions.FakeProvider
                ? provider.GetRequiredService<FakeOcrService>()
                : provider.GetRequiredService<NotConfiguredOcrService>();
        });
    }

    private static void AddZalo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ZaloOptions>().Bind(configuration.GetSection(ZaloOptions.SectionName));
        services.AddHttpClient<GraphZaloOpenApi>((provider, http) =>
        {
            var zalo = provider.GetRequiredService<IOptions<ZaloOptions>>().Value;
            http.BaseAddress = new Uri(zalo.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(zalo.TimeoutSeconds);
        });
        services.AddSingleton<FakeZaloOpenApi>();
        services.AddSingleton<NotConfiguredZaloOpenApi>();
        // Unset fails closed (MSG04, nobody is linked) instead of trusting tokens nobody checked.
        services.AddTransient<IZaloOpenApi>(provider => provider.GetRequiredService<IOptions<ZaloOptions>>().Value switch
        {
            { Provider: ZaloOptions.GraphProvider, AppSecret: var secret } when string.IsNullOrWhiteSpace(secret) =>
                throw new InvalidOperationException(
                    $"{ZaloOptions.SectionName}:AppSecret is required when {ZaloOptions.SectionName}:Provider is Graph (user-secrets or environment variable)."),
            { Provider: ZaloOptions.GraphProvider } => provider.GetRequiredService<GraphZaloOpenApi>(),
            { Provider: ZaloOptions.FakeProvider } => provider.GetRequiredService<FakeZaloOpenApi>(),
            { Provider: "" } => provider.GetRequiredService<NotConfiguredZaloOpenApi>(),
            var other => throw UnknownProvider(ZaloOptions.SectionName, other.Provider),
        });
    }

    private static InvalidOperationException UnknownProvider(string section, string value) =>
        new($"Unknown {section}:Provider '{value}'. See the README for the supported providers.");

    private static void AddReadinessChecks(this IHealthChecksBuilder builder)
    {
        foreach (var kind in Enum.GetValues<ReadinessCheckKind>())
        {
            var tags = kind is ReadinessCheckKind.Database or ReadinessCheckKind.Migrations
                ? new[] { DeploymentReadinessCheck.Tag, DeploymentReadinessCheck.DatabaseTag }
                : new[] { DeploymentReadinessCheck.Tag };
            builder.AddTypeActivatedCheck<DeploymentReadinessCheck>(ToCheckName(kind), failureStatus: null, tags: tags, args: [kind]);
        }
    }

    // "StatusCatalog" → "status-catalog", the name listed by /health/ready when the check fails.
    private static string ToCheckName(ReadinessCheckKind kind) =>
        string.Concat(kind.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? $"-{char.ToLowerInvariant(c)}" : char.ToLowerInvariant(c).ToString()));
}
