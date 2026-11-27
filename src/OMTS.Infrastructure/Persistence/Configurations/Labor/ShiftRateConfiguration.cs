using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class ShiftRateConfiguration : IEntityTypeConfiguration<ShiftRate>
{
    public void Configure(EntityTypeBuilder<ShiftRate> builder)
    {
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.HasOne(x => x.JobType).WithMany(j => j.ShiftRates).HasForeignKey(x => x.JobTypeId);
        builder.HasIndex(x => new { x.JobTypeId, x.EffectiveFrom }).IsUnique();
        builder.ReferencesAccount(nameof(ShiftRate.EnteredByAccountId));
        builder.ToTable(t => t.HasCheckConstraint("CK_ShiftRates_Amount", "[Amount] > 0"));
    }
}
