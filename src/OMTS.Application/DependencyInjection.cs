using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Cooperatives;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Purchasing.Suppliers;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Products;
using OMTS.Application.Features.Verification;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality;

namespace OMTS.Application;

public static class DependencyInjection
{
    /// <summary>Registers application-layer services (use cases, validators).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        services.AddSingleton(provider => new UnknownLoginCounter(timeProvider: provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<AuthService>();

        services.AddSingleton<PermissionCache>();
        services.AddSingleton<IPermissionCacheInvalidator>(provider => provider.GetRequiredService<PermissionCache>());
        services.AddScoped<PermissionService>();

        services.AddScoped<AmbientActor>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<StatusChangeService>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<MandatorySmsQueue>();
        services.AddScoped<OwnerActorRecipientFilter>();
        services.AddScoped<NotificationInboxService>();
        services.AddScoped<RowVersionGuard>();

        // ACS – personal accounts
        services.AddScoped<VerificationThrottle>();
        services.AddSingleton<VerificationStartGate>();
        services.AddScoped<VerificationService>();
        services.AddScoped<TraderCustomerLinker>();
        services.AddScoped<UsernameGenerator>();
        services.AddScoped<RegistrationService>();
        services.AddScoped<PasswordRecoveryService>();
        services.AddScoped<CurrentPasswordProof>();
        services.AddScoped<ContactChangeNotifier>();
        services.AddScoped<PasswordChangeService>();
        services.AddScoped<PhoneVerificationService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<NotificationPreferenceService>();
        services.AddScoped<PushDeviceService>();

        // ACS – Owner's management of staff accounts, zone access and roles
        services.AddScoped<AccessGrantService>();
        services.AddScoped<StaffAccountService>();
        services.AddScoped<RoleService>();

        // FARM-01 cooperative profile, LAB-01 employees
        services.AddScoped<CooperativeProfileViews>();
        services.AddScoped<CooperativeProfileService>();
        services.AddScoped<CooperativeCoverPhotoService>();
        services.AddScoped<CooperativeCertificationService>();
        services.AddScoped<EmployeeService>();

        // DEBT-01 receiving bank account
        services.AddScoped<ReceivingAccountAcceptance>();
        services.AddScoped<ReceivingBankAccountService>();

        // FARM-02 zones, FARM-03 zone licenses, FARM-04 rafts
        services.AddScoped<FarmingLookupAccess>();
        services.AddScoped<FarmingZoneService>();
        services.AddScoped<ZoneLicenseService>();
        // GFR-03 expiry reminders of zone licenses and cooperative certifications
        services.AddScoped<ZoneLicenseReminderService>();
        services.AddScoped<CertificationReminderService>();
        services.AddScoped<RaftService>();
        services.AddScoped<RaftReadService>();
        services.AddScoped<RaftViews>();
        services.AddScoped<RaftPhotoService>();
        services.AddScoped<RaftStatusService>();
        services.AddScoped<UploadedFileSaver>();
        services.AddScoped<OrphanFileCleanupService>();

        // INP – suppliers, farming inputs, purchase receipts, returns and supplier payments
        services.AddScoped<ZoneAccessGuard>();
        services.AddScoped<SupplierService>();
        services.AddScoped<FarmingInputItemService>();
        services.AddScoped<ProductService>();
        services.AddScoped<ProductReadService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<CustomerMergeService>();
        services.AddScoped<PurchaseReceiptAccess>();
        services.AddScoped<PurchaseReceiptValidator>();
        services.AddScoped<PurchaseReceiptViews>();
        services.AddScoped<PurchaseReceiptNotifications>();
        services.AddScoped<PurchaseReceiptService>();
        services.AddScoped<PurchaseReceiptCancellationService>();
        services.AddScoped<PurchaseReceiptCorrectionService>();
        services.AddScoped<PurchaseReceiptCopyService>();
        services.AddScoped<PurchaseReturnService>();
        services.AddScoped<PurchaseReceiptQueryService>();
        services.AddScoped<SupplierPaymentService>();
        services.AddScoped<SupplierDebtService>();

        // ENV-01…ENV-04 water quality monitoring and the overdue measurement reminder
        services.AddSingleton<EditingPresenceTracker>();
        services.AddScoped<WaterMeterRecognizer>();
        services.AddScoped<WaterAlertService>();
        services.AddScoped<WaterMeasurementViewBuilder>();
        services.AddScoped<WaterMeasurementRecordingService>();
        services.AddScoped<WaterMeasurementCorrectionService>();
        services.AddScoped<WaterMeasurementQueryService>();
        services.AddScoped<WaterThresholdService>();
        services.AddScoped<WaterMeasurementOverdueReminder>();
        services.AddScoped<WaterOverdueRearm>();

        return services;
    }
}
