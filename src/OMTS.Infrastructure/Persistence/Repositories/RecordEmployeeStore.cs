using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class RecordEmployeeStore(OmtsDbContext db) : IRecordEmployeeStore
{
    public void Add<TRow>(TRow row) where TRow : class, IRecordEmployee => db.Set<TRow>().Add(row);

    public void Remove<TRow>(TRow row) where TRow : class, IRecordEmployee => db.Set<TRow>().Remove(row);
}
