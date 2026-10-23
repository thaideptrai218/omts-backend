using OMTS.Domain.Entities.Purchasing;
using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.AfterSales;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Finance;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Entities.Traceability;

namespace OMTS.Infrastructure.Persistence;

public sealed class OmtsDbContext(DbContextOptions<OmtsDbContext> options) : DbContext(options)
{
    // Organization, identity and access control
    public DbSet<Cooperative> Cooperatives => Set<Cooperative>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeWorkingZone> EmployeeWorkingZones => Set<EmployeeWorkingZone>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<AccessGrant> AccessGrants => Set<AccessGrant>();
    public DbSet<LoginSession> LoginSessions => Set<LoginSession>();
    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();

    // Farming zones and rafts
    public DbSet<FarmingZone> FarmingZones => Set<FarmingZone>();
    public DbSet<ZoneComplianceDocument> ZoneComplianceDocuments => Set<ZoneComplianceDocument>();
    public DbSet<DocumentZoneScope> DocumentZoneScopes => Set<DocumentZoneScope>();
    public DbSet<Raft> Rafts => Set<Raft>();
    public DbSet<RaftLayoutVersion> RaftLayoutVersions => Set<RaftLayoutVersion>();
    public DbSet<Lane> Lanes => Set<Lane>();

    // Seed, cultivation and water measurements
    public DbSet<FarmingInputItem> FarmingInputItems => Set<FarmingInputItem>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseReceipt> PurchaseReceipts => Set<PurchaseReceipt>();
    public DbSet<PurchaseReceiptLine> PurchaseReceiptLines => Set<PurchaseReceiptLine>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<PurchaseReturnLine> PurchaseReturnLines => Set<PurchaseReturnLine>();
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<SeedReceiptLot> SeedReceiptLots => Set<SeedReceiptLot>();
    public DbSet<CultivationCycle> CultivationCycles => Set<CultivationCycle>();
    public DbSet<SeedingEvent> SeedingEvents => Set<SeedingEvent>();
    public DbSet<LaneSegment> LaneSegments => Set<LaneSegment>();
    public DbSet<CultivationLogEntry> CultivationLogEntries => Set<CultivationLogEntry>();
    public DbSet<WaterMeasurement> WaterMeasurements => Set<WaterMeasurement>();

    // Orders and sales
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SaleTransaction> SaleTransactions => Set<SaleTransaction>();
    public DbSet<PortWeighing> PortWeighings => Set<PortWeighing>();
    public DbSet<PortHandover> PortHandovers => Set<PortHandover>();

    // Harvest and traceability
    public DbSet<HarvestEvent> HarvestEvents => Set<HarvestEvent>();
    public DbSet<TraceabilityLot> TraceabilityLots => Set<TraceabilityLot>();
    public DbSet<TracePublicationVersion> TracePublicationVersions => Set<TracePublicationVersion>();
    public DbSet<TraceCorrection> TraceCorrections => Set<TraceCorrection>();

    // Post-sale
    public DbSet<QualityClaim> QualityClaims => Set<QualityClaim>();
    public DbSet<SaleAdjustment> SaleAdjustments => Set<SaleAdjustment>();
    public DbSet<PhysicalReturn> PhysicalReturns => Set<PhysicalReturn>();

    // Customer finance
    public DbSet<CustomerReceipt> CustomerReceipts => Set<CustomerReceipt>();
    public DbSet<ReceiptAllocation> ReceiptAllocations => Set<ReceiptAllocation>();
    public DbSet<CustomerCredit> CustomerCredits => Set<CustomerCredit>();
    public DbSet<CustomerRefund> CustomerRefunds => Set<CustomerRefund>();

    // Labor
    public DbSet<Contractor> Contractors => Set<Contractor>();
    public DbSet<SeasonalWorker> SeasonalWorkers => Set<SeasonalWorker>();
    public DbSet<WorkAssignment> WorkAssignments => Set<WorkAssignment>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<PayRuleVersion> PayRuleVersions => Set<PayRuleVersion>();
    public DbSet<PayLine> PayLines => Set<PayLine>();
    public DbSet<LaborSettlement> LaborSettlements => Set<LaborSettlement>();
    public DbSet<LaborPayment> LaborPayments => Set<LaborPayment>();
    public DbSet<Payroll> Payrolls => Set<Payroll>();

    // System
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<MessageDeliveryAttempt> MessageDeliveryAttempts => Set<MessageDeliveryAttempt>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Status> Statuses => Set<Status>();
    public DbSet<StatusTransition> StatusTransitions => Set<StatusTransition>();

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
