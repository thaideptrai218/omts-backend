using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Configurations.Purchasing;

internal sealed class PurchaseReturnConfiguration : IEntityTypeConfiguration<PurchaseReturn>
{
    public void Configure(EntityTypeBuilder<PurchaseReturn> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Receipt).WithMany(r => r.Returns).HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(PurchaseReturn.RecordedByAccountId));
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.RefundAmount).HasPrecision(18, 0);
        builder.Property(x => x.DebtReductionAmount).HasPrecision(18, 0);
        builder.HasThousandVndCheck(nameof(PurchaseReturn.RefundAmount));
        builder.HasThousandVndCheck(nameof(PurchaseReturn.DebtReductionAmount));
    }
}
