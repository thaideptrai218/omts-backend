namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The cycle facts other modules need (WATER GFR-04); only the cycle dates, never a tracked row.</summary>
public interface ICultivationCycleQuery
{
    /// <summary>The earliest start date among the Open cycles of each zone that has one (GFR-04); other zones are left out.</summary>
    Task<IReadOnlyDictionary<Guid, DateOnly>> EarliestOpenStartByZoneAsync(CancellationToken cancellationToken);
}
