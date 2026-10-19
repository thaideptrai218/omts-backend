using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

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
