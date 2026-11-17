using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        // Unique regardless of letter case: the column uses the case-insensitive collation of the database.
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PurchaseRequest).WithMany().HasForeignKey(x => x.PurchaseRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        builder.ReferencesAccount(
            nameof(SalesOrder.ReceivedByAccountId), nameof(SalesOrder.CreatedByAccountId), nameof(SalesOrder.CompletedByAccountId), nameof(SalesOrder.CancelledByAccountId));
        builder.Property(x => x.DiscountAmount).HasPrecision(18, 0);
        builder.Property(x => x.Subtotal).HasPrecision(18, 0);
        builder.Property(x => x.Total).HasPrecision(18, 0);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();

        // One request makes at most one order.
        builder.HasIndex(x => x.PurchaseRequestId).IsUnique().HasFilter("[PurchaseRequestId] IS NOT NULL");

        // The list: by status and day, by zone, by customer, by pickup day.
        builder.HasIndex(x => new { x.StatusId, x.OrderDate });
        builder.HasIndex(x => x.ZoneId);
        builder.HasIndex(x => x.CustomerId);
        builder.HasIndex(x => x.PickupDate);
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_SalesOrder_Amounts", "[DiscountAmount] >= 0 AND [Subtotal] >= 0 AND [Total] >= 0 AND [DiscountAmount] <= [Subtotal] AND [Total] = [Subtotal] - [DiscountAmount]"));
    }
}

internal sealed class SalesOrderLineConfiguration : IEntityTypeConfiguration<SalesOrderLine>
{
    public void Configure(EntityTypeBuilder<SalesOrderLine> builder)
    {
        builder.HasOne(x => x.Order).WithMany(o => o.Lines).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        // Not unique: an edit may swap the numbers of two lines, which one save cannot do under a unique index.
        builder.HasIndex(x => new { x.OrderId, x.LineNumber });
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ProductId);
        // The unit is copied (code, name, factor), with no foreign key, so it can leave the product or the unit list later without touching the order.
        builder.Property(x => x.UnitCode).HasMaxLength(Lengths.Code);
        builder.Property(x => x.UnitName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Factor).HasPrecision(18, 3);
        builder.Property(x => x.Quantity).HasPrecision(18, 3);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 0);
        builder.Property(x => x.ListPrice).HasPrecision(18, 0);
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Url);
        builder.ToTable(t => t.HasCheckConstraint("CK_SalesOrderLine_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [Amount] >= 0"));
    }
}
