using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class CooperativeConfiguration : IEntityTypeConfiguration<Cooperative>
{
    public void Configure(EntityTypeBuilder<Cooperative> builder)
    {
        // FARM-01 allows 255 characters for names.
        builder.Property(x => x.FullName).HasMaxLength(255);
        builder.Property(x => x.TaxCode).HasMaxLength(Lengths.Phone);
        builder.HasIndex(x => x.TaxCode).IsUnique();
        builder.Property(x => x.OfficeAddress).HasMaxLength(Lengths.Address);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.Email).HasMaxLength(Lengths.Email);
        builder.Property(x => x.Website).HasMaxLength(Lengths.Url);
        builder.Property(x => x.LegalRepresentative).HasMaxLength(255);
        builder.Property(x => x.BusinessLines).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.LogoUrl).HasMaxLength(Lengths.Url);

        builder.OwnsOne(x => x.BankAccount, bank =>
        {
            bank.Property(b => b.BankName).HasMaxLength(Lengths.Name);
            bank.Property(b => b.AccountNumber).HasMaxLength(Lengths.Code);
            bank.Property(b => b.AccountHolderName).HasMaxLength(Lengths.Name);
        });
    }
}
