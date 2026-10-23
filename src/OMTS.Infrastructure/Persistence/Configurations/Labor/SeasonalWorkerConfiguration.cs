using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class SeasonalWorkerConfiguration : IEntityTypeConfiguration<SeasonalWorker>
{
    public void Configure(EntityTypeBuilder<SeasonalWorker> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Contractor).WithMany(t => t.Members).HasForeignKey(x => x.ContractorId);
        builder.Property(x => x.FullName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.CitizenId).HasMaxLength(Lengths.Phone);

        // One record per person (LAB-02): parallel saves of the same person cannot both pass the pre-check.
        builder.HasIndex(x => x.CitizenId).IsUnique().HasFilter("[CitizenId] IS NOT NULL");
        builder.HasIndex(x => new { x.FullName, x.PhoneNumber }).IsUnique().HasFilter("[PhoneNumber] IS NOT NULL");
    }
}
