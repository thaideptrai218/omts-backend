using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class DocumentZoneScopeConfiguration : IEntityTypeConfiguration<DocumentZoneScope>
{
    public void Configure(EntityTypeBuilder<DocumentZoneScope> builder)
    {
        builder.HasKey(x => new { x.DocumentId, x.ZoneId });
        builder.HasOne(x => x.Document).WithMany(d => d.ZoneScopes).HasForeignKey(x => x.DocumentId);
        builder.HasOne(x => x.Zone).WithMany(z => z.DocumentScopes).HasForeignKey(x => x.ZoneId);
    }
}
