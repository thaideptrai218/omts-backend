using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.HasOne(x => x.Cooperative).WithMany(c => c.Customers).HasForeignKey(x => x.CooperativeId);
        builder.HasOne(x => x.Account).WithOne(a => a.Customer).HasForeignKey<Customer>(x => x.AccountId);
        builder.HasOne(x => x.MergedIntoCustomer).WithMany().HasForeignKey(x => x.MergedIntoCustomerId);

        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.HasIndex(x => x.PhoneNumber, "IX_Customers_PhoneNumber");

        // BR-SALE-04: a number is unique among the numbers staff saved; registration may repeat one until the profiles are
        // merged, and a merged profile no longer holds its number.
        builder.HasIndex(x => x.PhoneNumber, "UX_Customers_PhoneNumber_CheckedByStaff")
            .IsUnique()
            .HasFilter("[PhoneCheckedByStaff] = 1 AND [MergedIntoCustomerId] IS NULL AND [PhoneNumber] IS NOT NULL");

        builder.Property(x => x.Email).HasMaxLength(Lengths.Email);
        builder.Property(x => x.Address).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.ZaloAccountId).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.ZaloAccountId).IsUnique();
    }
}
