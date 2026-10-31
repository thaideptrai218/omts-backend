using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Configurations.Platform;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasOne(x => x.ActorAccount).WithMany().HasForeignKey(x => x.ActorAccountId);
        builder.Property(x => x.Action).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RecordType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RecordCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);

        // ACCOUNT-10 searches a period, newest first, alone or with one filter: each filter leads an index that
        // ends with the time, so the period is a range inside the seek.
        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => new { x.ZoneId, x.OccurredAt });
        builder.HasIndex(x => new { x.ActorAccountId, x.OccurredAt });
        builder.HasIndex(x => new { x.ActorEmployeeId, x.OccurredAt });
        builder.HasIndex(x => new { x.Action, x.OccurredAt });
        builder.HasIndex(x => new { x.RecordType, x.OccurredAt });
        // For the record code prefix. The prefix is a parameter, so SQL Server seeks only "RecordCode IS NOT NULL" and
        // reads every coded event of the log under a reused plan (QA 0.6 #3, deferred: matters at scale only).
        builder.HasIndex(x => new { x.RecordCode, x.OccurredAt });
    }
}
