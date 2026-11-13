using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Verification;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Application.Features.Sales.QualityClaims;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.HealthChecks;
using OMTS.Infrastructure.Identity;
using OMTS.Infrastructure.Identity.Revocation;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Ocr;
using OMTS.Infrastructure.Payments;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Interceptors;
using OMTS.Infrastructure.Persistence.Queries;
using OMTS.Infrastructure.Persistence.Repositories;
using OMTS.Infrastructure.Persistence.Repositories.Sales;
using OMTS.Infrastructure.Persistence.Repositories.Workforce;
using OMTS.Infrastructure.Persistence.Seed;
using OMTS.Infrastructure.Reports;
using OMTS.Infrastructure.Storage;
using OMTS.Infrastructure.Zalo;
using StackExchange.Redis;

namespace OMTS.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "OmtsDb";

    /// <summary>Registers persistence and external adapters.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<PermissionCacheInvalidationInterceptor>();
        services.AddSingleton<SessionRevocationInterceptor>();
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
        services.AddScoped<IDocumentNumberAllocator, DocumentNumberAllocator>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IWorkDayPlanRepository, WorkDayPlanRepository>();
        services.AddScoped<ILoginSessionRepository, LoginSessionRepository>();
        services.AddScoped<IAccessGrantRepository, AccessGrantRepository>();
        services.AddScoped<IFarmingZoneRepository, FarmingZoneRepository>();
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IAuditEventQuery, AuditEventQuery>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IRelatedRecordQuery, RelatedRecordQuery>();
        services.AddScoped<ISentInvoiceQrCodeQuery, SentInvoiceQrCodeQuery>();
        services.AddScoped<IZoneRecipientQuery, ZoneRecipientQuery>();
        services.AddScoped<IMandatorySmsRecipientQuery, MandatorySmsRecipientQuery>();
        services.AddScoped<IVerificationCodeRepository, VerificationCodeRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ICustomerReferences, CustomerReferences>();
        services.AddScoped<ICustomerListQuery, CustomerListReader>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IPurchaseRequestRepository, PurchaseRequestRepository>();
        services.AddScoped<ICooperativeOwnerQuery, CooperativeOwnerQuery>();
        services.AddScoped<ICooperativeRepository, CooperativeRepository>();
        services.AddScoped<IPushDeviceRepository, PushDeviceRepository>();
        services.AddScoped<IEmployeeRepository, EmployeeRepository>();
        services.AddScoped<ISalesProductRepository, SalesProductRepository>();
        services.AddScoped<IEmployeeWorkRecords, EmployeeWorkRecords>();
        services.AddScoped<IEmployeeAppRepository, EmployeeAppRepository>();
        services.AddScoped<IJobTypeRepository, JobTypeRepository>();
        services.AddScoped<IAttendanceSettingsRepository, AttendanceSettingsRepository>();
        services.AddScoped<IPayrollRepository, PayrollRepository>();
        services.AddScoped<IPayrollSheetRepository, PayrollSheetRepository>();
        services.AddScoped<IPayrollWorkReader, PayrollWorkReader>();
        services.AddScoped<IWagePaymentRepository, WagePaymentRepository>();
        services.AddScoped<IPayrollReader, PayrollReader>();
        services.AddScoped<IWorkAssignmentRepository, WorkAssignmentRepository>();
        services.AddScoped<IAttendanceRepository, AttendanceRepository>();
        services.AddScoped<IEmployeeRequestRepository, EmployeeRequestRepository>();
        services.AddScoped<IWorkEmployeeReader, WorkEmployeeReader>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IRaftRepository, RaftRepository>();
        services.AddScoped<ILaneIncidentRepository, LaneIncidentRepository>();
        services.AddScoped<ILaneProgressRepository, LaneProgressRepository>();
        services.AddScoped<INurseryBatchRepository, NurseryBatchRepository>();
        services.AddScoped<ICultivationCycleRepository, CultivationCycleRepository>();
        services.AddScoped<ICultivationLogRepository, CultivationLogRepository>();
        services.AddScoped<IMaterialUsageRepository, MaterialUsageRepository>();
        services.AddScoped<ISeedingRecordRepository, SeedingRecordRepository>();
        services.AddScoped<IHarvestLotRepository, HarvestLotRepository>();
        services.AddScoped<IHarvestLotSalesQuery, HarvestLotSalesQuery>();
        services.AddScoped<ILotTraceQuery, LotTraceQuery>();
        services.AddScoped<IProvenancePageQuery, ProvenancePageQuery>();
        services.AddScoped<IPublicOriginQuery, PublicOriginQuery>();
        services.AddScoped<IWithdrawalNoticeQuery, WithdrawalNoticeQuery>();
        services.AddScoped<IPurchaseRequestFulfilment, PurchaseRequestFulfilmentQuery>();
        services.AddScoped<IQualityClaimRepository, QualityClaimRepository>();
        services.AddScoped<IInvoiceSalesReturnCheck, NoSalesReturnsYetCheck>();
        services.AddScoped<ISalesInvoiceRepository, SalesInvoiceRepository>();
        services.AddScoped<IPortWeighingRepository, Persistence.Repositories.Sales.PortWeighingRepository>();
        services.AddScoped<ISalesInvoiceSearchReader, SalesInvoiceSearchReader>();
        services.AddScoped<IOperationsDashboardQuery, OperationsDashboardQuery>();
        services.AddScoped<ICustomerPaymentRepository, CustomerPaymentRepository>();
        services.AddScoped<ICustomerDebtAdjustmentRepository, CustomerDebtAdjustmentRepository>();
        services.AddScoped<IPendingSaleRepository, PendingSaleRepository>();
        services.AddOptions<ProvenanceOptions>()
            .Bind(configuration.GetSection(ProvenanceOptions.SectionName))
            .Validate(o => o.HasWebAddress(), ProvenanceOptions.InvalidPublicBaseUrl)
            .ValidateOnStart();
        services.AddScoped<ICustomerDebtQuery, CustomerDebtQuery>();
        services.AddScoped<SeedLotRepository>();
        services.AddScoped<ISeedLotRepository>(provider => provider.GetRequiredService<SeedLotRepository>());
        services.AddScoped<ISeedLotUsageQuery>(provider => provider.GetRequiredService<SeedLotRepository>());
        services.AddScoped<ICultivationCycleQuery, CultivationCycleQuery>();
        services.AddScoped<IZoneLicenseRepository, ZoneLicenseRepository>();
        services.AddScoped<IRaftMaterialCostQuery, RaftMaterialCostQuery>();
        services.AddScoped<IStoredFileReferenceQuery, StoredFileReferenceQuery>();
        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<IFarmingInputItemRepository, FarmingInputItemRepository>();
        services.AddScoped<IInputStockRepository, InputStockRepository>();
        services.AddScoped<IInputUnitRepository, InputUnitRepository>();
        services.AddScoped<IInputItemCategoryRepository, InputItemCategoryRepository>();
        services.AddScoped<IInputItemListQuery, InputItemListReader>();
        services.AddScoped<IInputIssueRepository, InputIssueRepository>();
        services.AddScoped<IInputIssueTypeRepository, InputIssueTypeRepository>();
        services.AddScoped<IInputMovementQuery, InputMovementReader>();
        services.AddScoped<IPurchaseReceiptRepository, PurchaseReceiptRepository>();
        services.AddScoped<ISupplierPaymentRepository, SupplierPaymentRepository>();
        services.AddScoped<IPurchaseDocumentCodeQuery, PurchaseDocumentCodeQuery>();
        services.AddScoped<ISupplierDebtQuery, SupplierDebtQuery>();
        services.AddScoped<ICashAccountRepository, CashAccountRepository>();
        services.AddScoped<ICashVoucherTypeRepository, CashVoucherTypeRepository>();
        services.AddScoped<ICashVoucherRepository, CashVoucherRepository>();
        services.AddScoped<ICashPartyRepository, CashPartyRepository>();
        services.AddScoped<ICashBookQuery, CashBookReader>();
        services.AddScoped<IWaterMeasurementRepository, WaterMeasurementRepository>();
        services.AddSingleton<IStatusCatalog, StatusCatalog>();

        // The staff and the employee app schemes tell their tokens apart by audience (and signing key): one audience for both would
        // let a token of one kind through the other's check, so the host does not start.
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.EmployeeAudience) && !string.Equals(o.EmployeeAudience.TrimEnd('/'), o.Audience.TrimEnd('/'), StringComparison.OrdinalIgnoreCase),
                $"{JwtOptions.SectionName}:EmployeeAudience must be set and differ from {JwtOptions.SectionName}:Audience.")
            .ValidateOnStart();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSessionRevocation(configuration);
        services.AddSingleton<IVerificationCodeHasher, HmacVerificationCodeHasher>();
        services.AddSingleton<IAttendanceQrCodes, HmacAttendanceQrCodes>();
        services.AddOptions<VerificationLimitOptions>().Bind(configuration.GetSection(VerificationLimitOptions.SectionName));
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        services.AddMessaging(configuration);
        services.AddFileStorage(configuration);
        services.AddOcr(configuration);
        services.AddZalo(configuration);
        services.AddPayments(configuration);
        services.AddZalo(configuration);
        services.AddSingleton<ISpreadsheetWriter, ClosedXmlSpreadsheetWriter>();
        services.AddSingleton<ISpreadsheetReader, ClosedXmlSpreadsheetReader>();

        services.AddScoped<DeploymentBootstrapper>();
        services.AddScoped<OwnerPasswordReset>();

        // FARM-03 expiring zone license reminder (formerly JOB-06)
        services.AddOptions<ZoneLicenseReminderOptions>().Bind(configuration.GetSection(ZoneLicenseReminderOptions.SectionName));
        services.AddSingleton<ZoneLicenseReminderJob>();
        services.AddHostedService<ZoneLicenseReminderHostedService>();

        // Overdue water measurement reminder (formerly JOB-05)
        services.AddOptions<WaterMeasurementOverdueOptions>().Bind(configuration.GetSection(WaterMeasurementOverdueOptions.SectionName));
        services.AddSingleton<WaterMeasurementOverdueJob>();
        services.AddHostedService<WaterMeasurementOverdueHostedService>();

        // LABOR-01: Left from the day after the last working day
        services.AddOptions<EmploymentEndOptions>().Bind(configuration.GetSection(EmploymentEndOptions.SectionName));
        services.AddSingleton<EmploymentEndJob>();
        services.AddHostedService<EmploymentEndHostedService>();

        // GFR-05: harvest lots still for sale after the days before automatic close, and the warning the day before
        services.AddOptions<HarvestLotAutoCloseOptions>().Bind(configuration.GetSection(HarvestLotAutoCloseOptions.SectionName));
        services.AddSingleton<HarvestLotAutoCloseJob>();
        // Council of 03/11 (PM answer 12/11/2026): the sales awaiting a VietQR transfer are stopped at the end of the business day, by the hosted service just before the close check
        services.AddSingleton<PendingSaleEndOfDayStopJob>();
        services.AddSingleton<OMTS.Application.Features.Harvest.IHarvestAutoCloseSchedule, HarvestAutoCloseSchedule>();
        services.AddHostedService<HarvestLotAutoCloseHostedService>();

        // Raft photos, license files, cover photos and certificate files no record points to (cooperative logos are never touched)
        services.AddOptions<OrphanFileCleanupOptions>()
            .Bind(configuration.GetSection(OrphanFileCleanupOptions.SectionName))
            .Validate(o => o.GracePeriod >= OrphanFileCleanupOptions.MinimumGracePeriod, $"{OrphanFileCleanupOptions.SectionName}:GracePeriod must be at least 01:00:00.")
            .ValidateOnStart();
        services.AddSingleton<OrphanFileCleanupJob>();
        services.AddHostedService<OrphanFileCleanupHostedService>();

        // Sessions that expired or were revoked long ago (and the device, address and browser they carry)
        services.AddOptions<LoginSessionCleanupOptions>()
            .Bind(configuration.GetSection(LoginSessionCleanupOptions.SectionName))
            .Validate(o => o.Retention >= LoginSessionCleanupOptions.MinimumRetention, $"{LoginSessionCleanupOptions.SectionName}:Retention must be at least 1.00:00:00.")
            .ValidateOnStart();
        services.AddSingleton<LoginSessionCleanupJob>();
        services.AddHostedService<LoginSessionCleanupHostedService>();
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
            provider.GetRequiredService<SessionRevocationInterceptor>(),
            provider.GetRequiredService<PushOutboxInterceptor>());

    // Redis or the database is picked when first resolved, from the final configuration. Without a Redis connection
    // string (and with Mode=Sql) nothing connects to Redis and every request is checked against the database.
    private static void AddSessionRevocation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionRevocationOptions>()
            .Bind(configuration.GetSection(SessionRevocationOptions.SectionName))
            .Configure(o => o.RedisConnectionString = configuration.GetConnectionString(SessionRevocationOptions.RedisConnectionStringName))
            .Validate(o => o.IsValid(out _), $"Invalid {SessionRevocationOptions.SectionName} configuration; see the README.")
            .ValidateOnStart();

        services.AddSingleton<RevocationSweepSignal>();
        services.AddSingleton<RedisCircuitBreaker>();
        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var settings = provider.GetRequiredService<IOptions<SessionRevocationOptions>>().Value;
            var redis = ConfigurationOptions.Parse(settings.RedisConnectionString!);
            // Never fail the host because Redis is down, and never queue commands for a Redis that is not there.
            redis.AbortOnConnectFail = false;
            redis.BacklogPolicy = BacklogPolicy.FailFast;
            redis.ConnectTimeout = (int)Math.Max(settings.CommandTimeout.TotalMilliseconds, 2000);
            redis.SyncTimeout = (int)settings.CommandTimeout.TotalMilliseconds;
            redis.AsyncTimeout = (int)settings.CommandTimeout.TotalMilliseconds;
            return ConnectionMultiplexer.Connect(redis);
        });
        services.AddSingleton<RedisSessionRevocationStore>();
        services.AddSingleton<SqlSessionRevocationStore>();
        services.AddSingleton<ISessionRevocationStore>(provider => provider.GetRequiredService<IOptions<SessionRevocationOptions>>().Value.UseRedis
            ? provider.GetRequiredService<RedisSessionRevocationStore>()
            : provider.GetRequiredService<SqlSessionRevocationStore>());
        services.AddHostedService<SessionRevocationSweeper>();
    }

    // Providers are picked when first resolved, from the final configuration (tests and environments
    // override it after this method runs).
    private static void AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.SectionName));
        services.AddOptions<SmsOptions>()
            .Bind(configuration.GetSection(SmsOptions.SectionName))
            .Validate(
                o => o.MandatoryTimeoutSeconds is >= SmsOptions.MinMandatoryTimeoutSeconds and <= SmsOptions.MaxMandatoryTimeoutSeconds,
                $"{SmsOptions.SectionName}:MandatoryTimeoutSeconds must be from {SmsOptions.MinMandatoryTimeoutSeconds} to {SmsOptions.MaxMandatoryTimeoutSeconds}.")
            .ValidateOnStart();
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
        services.AddSingleton<ITraderMessageSender, SmsTraderMessageSender>();
        services.AddSingleton<IPushSender>(provider => provider.GetRequiredService<IOptions<PushOptions>>().Value.Provider switch
        {
            PushOptions.FakeProvider => provider.GetRequiredService<FakePushSender>(),
            PushOptions.NoneProvider => ActivatorUtilities.CreateInstance<UnconfiguredPushSender>(provider),
            var other => throw UnknownProvider(PushOptions.SectionName, other),
        });

        services.AddScoped<PushOutbox>();
        services.AddScoped<IPushOutbox>(provider => provider.GetRequiredService<PushOutbox>());
        services.AddScoped<IMandatorySmsOutbox>(provider => provider.GetRequiredService<PushOutbox>());
        services.AddSingleton<MandatorySmsDelivery>();

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

    private static void AddPayments(this IServiceCollection services, IConfiguration configuration)
    {
        // A text that is no number already fails the binding, and ValidateOnStart brings that to startup too.
        services.AddOptions<PaymentNotificationOptions>()
            .Bind(configuration.GetSection(PaymentNotificationOptions.SectionName))
            .Validate(
                o => o.TimeoutSeconds is >= PaymentNotificationOptions.MinTimeoutSeconds and <= PaymentNotificationOptions.MaxTimeoutSeconds,
                $"{PaymentNotificationOptions.SectionName}:TimeoutSeconds must be from {PaymentNotificationOptions.MinTimeoutSeconds} to {PaymentNotificationOptions.MaxTimeoutSeconds} seconds.")
            .ValidateOnStart();
        services.AddOptions<PaymentProviderOptions>().Bind(configuration.GetSection(PaymentProviderOptions.SectionName));
        services.AddSingleton<FakePaymentNotificationService>();
        services.AddSingleton<IPaymentNotificationService>(provider =>
        {
            var name = provider.GetRequiredService<IOptions<PaymentProviderOptions>>().Value.Provider;
            return name switch
            {
                PaymentProviderOptions.FakeProvider when provider.GetRequiredService<IHostEnvironment>().IsDevelopment() =>
                    provider.GetRequiredService<FakePaymentNotificationService>(),
                PaymentProviderOptions.FakeProvider or PaymentProviderOptions.NoneProvider => new UnconfiguredPaymentNotificationService(),
                var other => throw UnknownProvider(PaymentProviderOptions.SectionName, other),
            };
        });
        // API-13: the same provider reads the incoming notifications; no real provider yet.
        services.AddSingleton<FakeBankNotificationParser>();
        services.AddSingleton<IBankNotificationParser>(provider =>
        {
            var name = provider.GetRequiredService<IOptions<PaymentProviderOptions>>().Value.Provider;
            return name switch
            {
                PaymentProviderOptions.FakeProvider when provider.GetRequiredService<IHostEnvironment>().IsDevelopment() =>
                    provider.GetRequiredService<FakeBankNotificationParser>(),
                PaymentProviderOptions.FakeProvider or PaymentProviderOptions.NoneProvider => new UnconfiguredBankNotificationParser(),
                var other => throw UnknownProvider(PaymentProviderOptions.SectionName, other),
            };
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
        // Degraded, never Unhealthy: without Redis requests are slower but still correctly checked.
        builder.AddTypeActivatedCheck<SessionRevocationReadinessCheck>(
            SessionRevocationReadinessCheck.Name, failureStatus: HealthStatus.Degraded, tags: [DeploymentReadinessCheck.Tag]);

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


