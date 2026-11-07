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
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Labor;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Purchasing.Suppliers;
using OMTS.Application.Features.ReceivingBankAccounts;
using OMTS.Application.Features.Sales.Customers;
using OMTS.Application.Features.Sales.Products;
using OMTS.Application.Features.Sales.PurchaseRequests;
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
        services.AddScoped<AuditEventSearchService>();
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

        // FARM-01 cooperative profile, LABOR-01 employees
        services.AddScoped<CooperativeProfileViews>();
        services.AddScoped<CooperativeProfileService>();
        services.AddScoped<CooperativeCoverPhotoService>();
        services.AddScoped<CooperativeCertificationService>();
        services.AddScoped<EmployeeCallerAccess>();
        services.AddScoped<EmployeeViewBuilder>();
        services.AddScoped<EmployeeProfileValidator>();
        services.AddScoped<EmployeeQueryService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<EmployeeWorkingZoneService>();
        services.AddScoped<EmploymentService>();
        services.AddScoped<EmploymentEndService>();

        // DEBT-01 receiving bank account
        services.AddScoped<ReceivingAccountAcceptance>();
        services.AddScoped<ReceivingBankAccountService>();

        // LABOR-11 job types and rates, LABOR-15 attendance settings, LABOR-16 payroll settings, the pay period lock (BR-LABOR-13)
        services.AddScoped<PayPeriodLock>();
        services.AddScoped<JobTypeQueryService>();
        services.AddScoped<JobTypeService>();
        services.AddScoped<PayRateService>();
        services.AddScoped<AttendanceSettingsService>();
        services.AddScoped<PayrollSettingsService>();

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

        // CYCLE – cultivation cycles and the lane states derived from their records
        services.AddScoped<LaneStateReader>();
        services.AddScoped<CultivationCycleViews>();
        services.AddScoped<CycleWriteGuard>();
        services.AddScoped<CultivationLogViews>();
        services.AddScoped<CultivationLogPhotoStore>();
        services.AddScoped<CultivationLogService>();
        services.AddScoped<CultivationLogCorrectionService>();
        services.AddScoped<CultivationLogReadService>();
        services.AddScoped<CultivationCycleService>();
        services.AddScoped<CultivationCycleReadService>();
        services.AddScoped<CultivationProgressService>();

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
        services.AddScoped<IPurchaseRequestFulfilment, NoInvoicesYetFulfilment>();
        services.AddScoped<PurchaseRequestViews>();
        services.AddScoped<PurchaseRequestNotifications>();
        services.AddScoped<PurchaseRequestReadService>();
        services.AddScoped<TraderPurchaseRequestService>();
        services.AddScoped<PurchaseRequestResponseService>();
        services.AddScoped<TraderMessageService>();
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
        services.AddScoped<SupplierPaymentNotifications>();
        services.AddScoped<SupplierDebtService>();
        services.AddScoped<SupplierDebtExportService>();

        // ENV-01…ENV-04 water quality monitoring and the overdue measurement reminder
        services.AddSingleton<EditingPresenceTracker>();
        services.AddScoped<WaterMeterRecognizer>();
        services.AddScoped<WaterMeterPhotoStore>();
        services.AddScoped<WaterAlertService>();
        services.AddScoped<WaterCorrectionNotifier>();
        services.AddScoped<WaterCorrectionPreconditions>();
        services.AddScoped<WaterMeasurementViewBuilder>();
        services.AddScoped<WaterMeasurementRecordingService>();
        services.AddScoped<WaterMeasurementCorrectionService>();
        services.AddScoped<WaterMeasurementQueryService>();
        services.AddScoped<WaterThresholdService>();
        services.AddScoped<WaterMeasurementOverdueReminder>();
        services.AddScoped<WaterZoneOverviewService>();

        return services;
    }
}
