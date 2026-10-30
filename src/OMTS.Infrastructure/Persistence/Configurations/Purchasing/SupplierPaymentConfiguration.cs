using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Configurations.Purchasing;

internal sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> builder)
    {
        builder.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Receipt).WithMany(r => r.Payments).HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(SupplierPayment.RecordedByAccountId));
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.HasThousandVndCheck(nameof(SupplierPayment.Amount));
        builder.Property(x => x.ReferenceCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.SupplierId, x.PaymentDate });
    }
}
