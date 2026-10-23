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
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.Purchasing.Suppliers;
using OMTS.Application.Features.Verification;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application;

public static class DependencyInjection
{
    /// <summary>Registers application-layer services (use cases, validators).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        services.AddSingleton(_ => new UnknownLoginCounter());
        services.AddScoped<AuthService>();

        services.AddSingleton<PermissionCache>();
        services.AddSingleton<IPermissionCacheInvalidator>(provider => provider.GetRequiredService<PermissionCache>());
        services.AddScoped<PermissionService>();

        services.AddScoped<AmbientActor>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<RowVersionGuard>();

        // ACS – personal accounts
        services.AddScoped<VerificationThrottle>();
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
        services.AddScoped<CooperativeProfileService>();
        services.AddScoped<EmployeeService>();

        // FARM-02 zones, FARM-03 zone licenses, FARM-04 rafts
        services.AddScoped<FarmingLookupAccess>();
        services.AddScoped<FarmingZoneService>();
        services.AddScoped<ZoneLicenseService>();
        services.AddScoped<ZoneLicenseReminderService>();
        services.AddScoped<RaftService>();
        services.AddScoped<RaftConditionService>();

        // INP – suppliers, farming inputs, purchase receipts, returns and supplier payments
        services.AddScoped<ZoneAccessGuard>();
        services.AddScoped<SupplierService>();
        services.AddScoped<FarmingInputItemService>();
        services.AddScoped<PurchaseReceiptValidator>();
        services.AddScoped<PurchaseReceiptViews>();
        services.AddScoped<PurchaseReceiptService>();
        services.AddScoped<PurchaseReceiptCancellationService>();
        services.AddScoped<PurchaseReturnService>();
        services.AddScoped<PurchaseReceiptQueryService>();
        services.AddScoped<SupplierPaymentService>();
        services.AddScoped<SupplierDebtService>();

        return services;
    }
}
