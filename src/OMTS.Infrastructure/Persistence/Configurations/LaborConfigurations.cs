using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;

namespace OMTS.Infrastructure.Persistence.Configurations;

internal sealed class ContractorConfiguration : IEntityTypeConfiguration<Contractor>
{
    public void Configure(EntityTypeBuilder<Contractor> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.OtherContacts).HasMaxLength(Lengths.Address);
    }
}

internal sealed class SeasonalWorkerConfiguration : IEntityTypeConfiguration<SeasonalWorker>
{
    public void Configure(EntityTypeBuilder<SeasonalWorker> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.Property(x => x.FullName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.CitizenId).HasMaxLength(Lengths.Phone);
        builder.HasIndex(x => x.CitizenId).IsUnique();
        builder.HasIndex(x => new { x.FullName, x.PhoneNumber });
    }
}

internal sealed class WorkAssignmentConfiguration : IEntityTypeConfiguration<WorkAssignment>
{
    public void Configure(EntityTypeBuilder<WorkAssignment> builder)
    {
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.CycleId);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.HasOne(x => x.Contractor).WithMany().HasForeignKey(x => x.ContractorId);
        builder.ReferencesAccount(nameof(WorkAssignment.CancelledByAccountId));

        builder.Property(x => x.Task).HasMaxLength(Lengths.Address);
        builder.Property(x => x.CancellationReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.ZoneId, x.WorkDate });

        // Daily wage: exactly one employee or seasonal worker. Task contract: a contractor and an amount (E40).
        var oneWorker = ConfigurationHelpers.ExactlyOneNotNull(nameof(WorkAssignment.EmployeeId), nameof(WorkAssignment.WorkerId));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_WorkAssignment_PaymentType",
            $"([PaymentType] = N'DailyWage' AND {oneWorker}) OR " +
            "([PaymentType] = N'TaskContract' AND [ContractorId] IS NOT NULL AND [ContractAmount] IS NOT NULL)"));
    }
}

internal sealed class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    public void Configure(EntityTypeBuilder<AttendanceRecord> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.HasOne(x => x.Contractor).WithMany().HasForeignKey(x => x.ContractorId);
        builder.ReferencesAccount(nameof(AttendanceRecord.RecordedByAccountId));
        builder.Property(x => x.Notes).HasMaxLength(Lengths.Reason);

        // One record per person per day (E41).
        builder.HasIndex(x => new { x.EmployeeId, x.WorkDate }).IsUnique().HasFilter("[EmployeeId] IS NOT NULL");
        builder.HasIndex(x => new { x.WorkerId, x.WorkDate }).IsUnique().HasFilter("[WorkerId] IS NOT NULL");
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AttendanceRecord_OnePerson",
            ConfigurationHelpers.ExactlyOneNotNull(nameof(AttendanceRecord.EmployeeId), nameof(AttendanceRecord.WorkerId))));
    }
}

internal sealed class PayRuleVersionConfiguration : IEntityTypeConfiguration<PayRuleVersion>
{
    public void Configure(EntityTypeBuilder<PayRuleVersion> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.ReferencesAccount(nameof(PayRuleVersion.EnteredByAccountId), nameof(PayRuleVersion.ApprovedByAccountId));
        builder.HasIndex(x => new { x.SubjectType, x.EmployeeId, x.ValidFrom });
    }
}

internal sealed class PayLineConfiguration : IEntityTypeConfiguration<PayLine>
{
    public void Configure(EntityTypeBuilder<PayLine> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.HasOne(x => x.Attendance).WithMany().HasForeignKey(x => x.AttendanceId);
        builder.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.AssignmentId);
        builder.HasOne(x => x.PayRuleVersion).WithMany().HasForeignKey(x => x.PayRuleVersionId);
        builder.HasOne(x => x.Settlement).WithMany(s => s.PayLines).HasForeignKey(x => x.SettlementId);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.HasThousandVndCheck(nameof(PayLine.Amount));
    }
}

internal sealed class LaborSettlementConfiguration : IEntityTypeConfiguration<LaborSettlement>
{
    public void Configure(EntityTypeBuilder<LaborSettlement> builder)
    {
        builder.HasOne(x => x.Contractor).WithMany().HasForeignKey(x => x.ContractorId);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.ReferencesAccount(nameof(LaborSettlement.PreparedByAccountId), nameof(LaborSettlement.ApprovedByAccountId));
        builder.HasThousandVndCheck(nameof(LaborSettlement.TotalObligation));
    }
}

internal sealed class LaborPaymentConfiguration : IEntityTypeConfiguration<LaborPayment>
{
    public void Configure(EntityTypeBuilder<LaborPayment> builder)
    {
        builder.HasOne(x => x.Settlement).WithMany(s => s.Payments).HasForeignKey(x => x.SettlementId);
        builder.HasOne(x => x.OriginalPayment).WithMany().HasForeignKey(x => x.OriginalPaymentId);
        builder.Property(x => x.Recipient).HasMaxLength(Lengths.Name);
        builder.Property(x => x.ReferenceCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.AdjustmentReason).HasMaxLength(Lengths.Reason);
        builder.HasThousandVndCheck(nameof(LaborPayment.AmountPaid));
    }
}
