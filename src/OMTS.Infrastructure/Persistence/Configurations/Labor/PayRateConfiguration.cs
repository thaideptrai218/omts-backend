using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class PayRateConfiguration : IEntityTypeConfiguration<PayRate>
{
    public void Configure(EntityTypeBuilder<PayRate> builder)
    {
        builder.Property(x => x.HourlyRate).HasPrecision(18, 0);
        builder.HasOne(x => x.JobType).WithMany(j => j.Rates).HasForeignKey(x => x.JobTypeId);
        builder.HasIndex(x => new { x.JobTypeId, x.EffectiveFrom }).IsUnique();
        builder.ReferencesAccount(nameof(PayRate.EnteredByAccountId));
        builder.ToTable(t => t.HasCheckConstraint("CK_PayRates_HourlyRate", "[HourlyRate] > 0"));
    }
}
