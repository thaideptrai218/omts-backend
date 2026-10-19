using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class ContractorConfiguration : IEntityTypeConfiguration<Contractor>
{
    public void Configure(EntityTypeBuilder<Contractor> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.OtherContacts).HasMaxLength(Lengths.Address);
    }
}
