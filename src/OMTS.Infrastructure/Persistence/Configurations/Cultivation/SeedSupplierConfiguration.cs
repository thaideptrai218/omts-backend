using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class SeedSupplierConfiguration : IEntityTypeConfiguration<SeedSupplier>
{
    public void Configure(EntityTypeBuilder<SeedSupplier> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.OtherContacts).HasMaxLength(Lengths.Address);
    }
}
