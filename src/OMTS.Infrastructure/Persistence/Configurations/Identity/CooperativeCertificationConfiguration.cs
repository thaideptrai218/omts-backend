using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class CooperativeCertificationConfiguration : IEntityTypeConfiguration<CooperativeCertification>
{
    public void Configure(EntityTypeBuilder<CooperativeCertification> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Number).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Issuer).HasMaxLength(Lengths.Name);
        builder.Property(x => x.FileUrl).HasMaxLength(Lengths.Url);

        builder.HasOne(x => x.Cooperative).WithMany(c => c.Certifications).HasForeignKey(x => x.CooperativeId);
    }
}
