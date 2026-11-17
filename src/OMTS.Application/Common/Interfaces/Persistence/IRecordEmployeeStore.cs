using OMTS.Domain.Common;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Adds and removes the rows of the employee tables of the nursery, seeding and harvest records (one store for the three, as their rows are alike).</summary>
public interface IRecordEmployeeStore
{
    void Add<TRow>(TRow row) where TRow : class, IRecordEmployee;

    void Remove<TRow>(TRow row) where TRow : class, IRecordEmployee;
}
