using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;
namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class JobTypeConfiguration : IEntityTypeConfiguration<JobType>
{
    public void Configure(EntityTypeBuilder<JobType> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasOne<OMTS.Domain.Entities.Platform.Status>().WithMany().HasForeignKey(x => x.StatusId);
    }
}
