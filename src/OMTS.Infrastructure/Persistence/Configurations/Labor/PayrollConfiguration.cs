using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class PayrollConfiguration : IEntityTypeConfiguration<Payroll>
{
    public void Configure(EntityTypeBuilder<Payroll> builder)
    {
        // Sheets of one period may exist side by side when they cover different employees, and a cancelled sheet leaves its period free,
        // so the period is not unique; that no employee is paid twice for a day is kept when a sheet is made and reloaded (LABOR-12).
        builder.HasIndex(x => new { x.PeriodStart, x.PeriodEnd });
        builder.HasIndex(x => x.PeriodEnd);
        // A header older than the sheets has no code yet.
        builder.HasIndex(x => x.Code).IsUnique().HasFilter("[Code] <> ''");
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.Property(x => x.ExpenseMonth).HasMaxLength(7);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.SendBackNote).HasMaxLength(Lengths.Reason);
        builder.Ignore(x => x.IsLocked);
        builder.Ignore(x => x.IsOpen);
        builder.ReferencesAccount(
            nameof(Payroll.SentByAccountId), nameof(Payroll.SentBackByAccountId), nameof(Payroll.FinalizedByAccountId), nameof(Payroll.CancelledByAccountId));
        builder.HasMany(x => x.Lines).WithOne(x => x.Payroll).HasForeignKey(x => x.PayrollId);
        builder.HasMany(x => x.SelectedEmployees).WithOne(x => x.Payroll).HasForeignKey(x => x.PayrollId);
        builder.ToTable(t => t.HasCheckConstraint("CK_Payrolls_Period", "[PeriodEnd] >= [PeriodStart]"));
    }
}

internal sealed class PayrollSelectedEmployeeConfiguration : IEntityTypeConfiguration<PayrollSelectedEmployee>
{
    public void Configure(EntityTypeBuilder<PayrollSelectedEmployee> builder)
    {
        builder.HasKey(x => new { x.PayrollId, x.EmployeeId });
        builder.HasOne<OMTS.Domain.Entities.Identity.Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.EmployeeId);
    }
}

internal sealed class PayrollLineConfiguration : IEntityTypeConfiguration<PayrollLine>
{
    public void Configure(EntityTypeBuilder<PayrollLine> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.Property(x => x.HourlyRateNote).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Hours).HasPrecision(18, 2);
        builder.Property(x => x.MainPay).HasPrecision(18, 0);
        builder.Property(x => x.Bonus).HasPrecision(18, 0);
        builder.Property(x => x.Deduction).HasPrecision(18, 0);
        builder.Ignore(x => x.Income);
        builder.Ignore(x => x.Net);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Jobs).WithOne(x => x.PayrollLine).HasForeignKey(x => x.PayrollLineId);
        // One payslip per employee in a sheet; every code is given once.
        builder.HasIndex(x => new { x.PayrollId, x.EmployeeId }).IsUnique();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => x.EmployeeId);
    }
}

internal sealed class PayrollLineJobConfiguration : IEntityTypeConfiguration<PayrollLineJob>
{
    public void Configure(EntityTypeBuilder<PayrollLineJob> builder)
    {
        builder.Property(x => x.JobName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.HourlyRate).HasPrecision(18, 0);
        builder.Property(x => x.Hours).HasPrecision(18, 2);
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.HasIndex(x => x.PayrollLineId);
    }
}

internal sealed class WagePaymentConfiguration : IEntityTypeConfiguration<WagePayment>
{
    public void Configure(EntityTypeBuilder<WagePayment> builder)
    {
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.RequestKey).HasMaxLength(64);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PayrollLine).WithMany().HasForeignKey(x => x.PayrollLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.CashVoucher).WithMany().HasForeignKey(x => x.CashVoucherId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(WagePayment.RecordedByAccountId));
        builder.HasMany(x => x.Deductions).WithOne(x => x.Advance).HasForeignKey(x => x.AdvanceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.CashVoucherId).IsUnique();
        builder.HasIndex(x => x.PayrollLineId);
        builder.HasIndex(x => new { x.EmployeeId, x.Kind });
        builder.HasIndex(x => x.PaidAt);
        // A repeated request with the same key creates nothing; parallel ones cannot both save.
        builder.HasIndex(x => new { x.RequestKey, x.RequestSequence }).IsUnique().HasFilter("[RequestKey] IS NOT NULL");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_WagePayments_Amount", "[Amount] > 0");
            t.HasCheckConstraint("CK_WagePayments_Line", "([Kind] = 'Wage' AND [PayrollLineId] IS NOT NULL) OR ([Kind] = 'Advance' AND [PayrollLineId] IS NULL)");
        });
    }
}

internal sealed class PayrollAdvanceDeductionConfiguration : IEntityTypeConfiguration<PayrollAdvanceDeduction>
{
    public void Configure(EntityTypeBuilder<PayrollAdvanceDeduction> builder)
    {
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.HasOne(x => x.PayrollLine).WithMany().HasForeignKey(x => x.PayrollLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.PayrollLineId);
        builder.HasIndex(x => x.AdvanceId);
        builder.ToTable(t => t.HasCheckConstraint("CK_PayrollAdvanceDeductions_Amount", "[Amount] > 0"));
    }
}
