using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Traceability;

namespace OMTS.Infrastructure.Persistence.Configurations.Traceability;

internal sealed class TracePublicationVersionConfiguration : IEntityTypeConfiguration<TracePublicationVersion>
{
    public void Configure(EntityTypeBuilder<TracePublicationVersion> builder)
    {
        builder.HasOne(x => x.Lot).WithMany(l => l.PublicationVersions).HasForeignKey(x => x.LotId);
        builder.HasIndex(x => new { x.LotId, x.VersionNumber }).IsUnique();
        builder.ReferencesAccount(
            nameof(TracePublicationVersion.ReviewedByAccountId),
            nameof(TracePublicationVersion.PublishedByAccountId),
            nameof(TracePublicationVersion.WithdrawnByAccountId));

        builder.Property(x => x.ReviewErrors).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.WithdrawalReason).HasMaxLength(Lengths.Reason);
    }
}
