using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Platform;
using OMTS.Infrastructure.Persistence.Seed;

namespace OMTS.Infrastructure.Persistence.Configurations.Platform;

internal sealed class StatusTransitionConfiguration : IEntityTypeConfiguration<StatusTransition>
{
    public void Configure(EntityTypeBuilder<StatusTransition> builder)
    {
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasOne(x => x.FromStatus).WithMany().HasForeignKey(x => x.FromStatusId);
        builder.HasOne(x => x.ToStatus).WithMany().HasForeignKey(x => x.ToStatusId);
        builder.HasIndex(x => new { x.FromStatusId, x.ToStatusId }).IsUnique();
        builder.Property(x => x.UseCases).HasMaxLength(Lengths.Code);
        builder.Property(x => x.AllowedPerformers).HasMaxLength(Lengths.Name);
        builder.Property(x => x.TriggerEvent).HasMaxLength(Lengths.Code);
        builder.Property(x => x.GuardCondition).HasMaxLength(Lengths.Reason);

        builder.HasData(StatusCatalogSeed.Transitions);
    }
}
