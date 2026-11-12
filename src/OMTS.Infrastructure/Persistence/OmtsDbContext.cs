using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Entities.CashBook;
using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence;

public sealed class OmtsDbContext(DbContextOptions<OmtsDbContext> options) : DbContext(options)
{
    // Organization, identity and access control
    public DbSet<Cooperative> Cooperatives => Set<Cooperative>();
    public DbSet<CooperativeCertification> CooperativeCertifications => Set<CooperativeCertification>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeWorkingZone> EmployeeWorkingZones => Set<EmployeeWorkingZone>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<AccessGrant> AccessGrants => Set<AccessGrant>();
    public DbSet<LoginSession> LoginSessions => Set<LoginSession>();
    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();

    // Farming zones and rafts
    public DbSet<FarmingZone> FarmingZones => Set<FarmingZone>();
    public DbSet<ZoneComplianceDocument> ZoneComplianceDocuments => Set<ZoneComplianceDocument>();
    public DbSet<Raft> Rafts => Set<Raft>();
    public DbSet<RaftPhoto> RaftPhotos => Set<RaftPhoto>();
    public DbSet<Lane> Lanes => Set<Lane>();

    // Seed, cultivation and water measurements
    public DbSet<FarmingInputItem> FarmingInputItems => Set<FarmingInputItem>();
    public DbSet<InputUnit> InputUnits => Set<InputUnit>();
    public DbSet<InputItemUnit> InputItemUnits => Set<InputItemUnit>();
    public DbSet<InputItemCategory> InputItemGroups => Set<InputItemCategory>();
    public DbSet<InputItemPhoto> InputItemPhotos => Set<InputItemPhoto>();
    public DbSet<InputStockMovement> InputStockMovements => Set<InputStockMovement>();
    public DbSet<InputIssueType> InputIssueTypes => Set<InputIssueType>();
    public DbSet<InputIssue> InputIssues => Set<InputIssue>();
    public DbSet<InputIssueLine> InputIssueLines => Set<InputIssueLine>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseReceipt> PurchaseReceipts => Set<PurchaseReceipt>();
    public DbSet<PurchaseReceiptLine> PurchaseReceiptLines => Set<PurchaseReceiptLine>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<PurchaseReturnLine> PurchaseReturnLines => Set<PurchaseReturnLine>();
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<SupplierPaymentAllocation> SupplierPaymentAllocations => Set<SupplierPaymentAllocation>();
    public DbSet<CashAccount> CashAccounts => Set<CashAccount>();
    public DbSet<CashVoucherType> CashVoucherTypes => Set<CashVoucherType>();
    public DbSet<CashVoucher> CashVouchers => Set<CashVoucher>();
    public DbSet<CashParty> CashParties => Set<CashParty>();
    public DbSet<CultivationCycle> CultivationCycles => Set<CultivationCycle>();
    public DbSet<CultivationLog> CultivationLogs => Set<CultivationLog>();
    public DbSet<MaterialUsage> MaterialUsages => Set<MaterialUsage>();
    public DbSet<CultivationLogPhoto> CultivationLogPhotos => Set<CultivationLogPhoto>();
    public DbSet<SeedingRecord> SeedingRecords => Set<SeedingRecord>();
    public DbSet<SeedingRecordLane> SeedingRecordLanes => Set<SeedingRecordLane>();
    public DbSet<WaterMeasurement> WaterMeasurements => Set<WaterMeasurement>();
    public DbSet<HarvestLot> HarvestLots => Set<HarvestLot>();
    public DbSet<HarvestLotLane> HarvestLotLanes => Set<HarvestLotLane>();
    public DbSet<HarvestLotPhoto> HarvestLotPhotos => Set<HarvestLotPhoto>();

    // Products and customers
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<PurchaseRequest> PurchaseRequests => Set<PurchaseRequest>();
    public DbSet<QualityClaim> QualityClaims => Set<QualityClaim>();
    public DbSet<QualityClaimPhoto> QualityClaimPhotos => Set<QualityClaimPhoto>();
    public DbSet<SalesInvoice> SalesInvoices => Set<SalesInvoice>();
    public DbSet<SalesInvoiceLine> SalesInvoiceLines => Set<SalesInvoiceLine>();
    public DbSet<PortWeighing> PortWeighings => Set<PortWeighing>();
    public DbSet<PortWeighingCage> PortWeighingCages => Set<PortWeighingCage>();
    public DbSet<SalesInvoicePriceCut> SalesInvoicePriceCuts => Set<SalesInvoicePriceCut>();
    public DbSet<CustomerPayment> CustomerPayments => Set<CustomerPayment>();
    public DbSet<CustomerPaymentAllocation> CustomerPaymentAllocations => Set<CustomerPaymentAllocation>();
    public DbSet<CustomerDebtAdjustment> CustomerDebtAdjustments => Set<CustomerDebtAdjustment>();
    public DbSet<PendingSale> PendingSales => Set<PendingSale>();

    // Labor (LABOR-01, LABOR-02, LABOR-04, LABOR-05, LABOR-10, LABOR-11, LABOR-15)
    public DbSet<EmployeeTypePeriod> EmployeeTypePeriods => Set<EmployeeTypePeriod>();
    public DbSet<JobType> JobTypes => Set<JobType>();
    public DbSet<EmployeeJobType> EmployeeJobTypes => Set<EmployeeJobType>();
    public DbSet<PayRate> PayRates => Set<PayRate>();
    public DbSet<AttendanceSettingsVersion> AttendanceSettingsVersions => Set<AttendanceSettingsVersion>();
    public DbSet<Payroll> Payrolls => Set<Payroll>();
    public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();
    public DbSet<PayrollLineJob> PayrollLineJobs => Set<PayrollLineJob>();
    public DbSet<PayrollSelectedEmployee> PayrollSelectedEmployees => Set<PayrollSelectedEmployee>();
    public DbSet<WagePayment> WagePayments => Set<WagePayment>();
    public DbSet<PayrollAdvanceDeduction> PayrollAdvanceDeductions => Set<PayrollAdvanceDeduction>();
    public DbSet<WorkAssignment> WorkAssignments => Set<WorkAssignment>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<EmployeeRequest> EmployeeRequests => Set<EmployeeRequest>();

    // System
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Status> Statuses => Set<Status>();
    public DbSet<StatusTransition> StatusTransitions => Set<StatusTransition>();
    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // VND amounts; weights, pH, salinity and coordinates override this per property.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        // Enums are stored by name so data stays readable and filters/check constraints can use the names.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(50);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OmtsDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(e => !e.IsOwned()))
        {
            if (typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType).Property(nameof(AuditableEntity.RowVersion)).IsRowVersion();
            }

            // Business data is never hard-deleted (records are discontinued, revoked or superseded), and
            // SQL Server rejects the many cascade paths this model would otherwise create.
            foreach (var foreignKey in entityType.GetForeignKeys().Where(fk => !fk.IsOwnership))
            {
                foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
            }
        }
    }
}


