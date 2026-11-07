using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class PayrollConfiguration : IEntityTypeConfiguration<Payroll>
{
    public void Configure(EntityTypeBuilder<Payroll> builder)
    {
        // A pay period has one payroll (BR-LABOR-10). That periods do not overlap is kept when a payroll is created (LABOR-12).
        builder.HasIndex(x => x.PeriodStart).IsUnique();
        builder.HasIndex(x => x.PeriodEnd).IsUnique();
        builder.Property(x => x.SendBackNote).HasMaxLength(Lengths.Reason);
        builder.Ignore(x => x.IsLocked);
        builder.ReferencesAccount(nameof(Payroll.SentByAccountId), nameof(Payroll.SentBackByAccountId), nameof(Payroll.FinalizedByAccountId));
        builder.ToTable(t => t.HasCheckConstraint("CK_Payrolls_Period", "[PeriodEnd] >= [PeriodStart]"));
    }
}
