using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Configurations.Purchasing;

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.HasIndex(x => x.PhoneNumber).IsUnique();
        builder.Property(x => x.Address).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Email).HasMaxLength(Lengths.Email);
        builder.Property(x => x.Province).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Ward).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Note).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.CompanyName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.TaxCode).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.IdentityNumber).HasMaxLength(Lengths.Phone);
    }
}
