using OMTS.Application.Features.Cultivation.Progress;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The cycle facts other modules need (FARM-04, WATER GFR-04, LABOR, HARVEST); only the cycle dates, never a tracked row.</summary>
public interface ICultivationCycleQuery
{
    /// <summary>The Open cycle of the raft, or null.</summary>
    Task<CycleSpan?> OpenCycleAsync(Guid raftId, CancellationToken cancellationToken);

    /// <summary>The cycle of the raft open on the date, the close day included, or null (BR-FARM-03).</summary>
    Task<CycleSpan?> CycleOnDateAsync(Guid raftId, DateOnly date, CancellationToken cancellationToken);

    /// <summary>The Open cycle of each of the rafts that has one, in one read; other rafts are left out.</summary>
    Task<IReadOnlyDictionary<Guid, CycleSpan>> OpenCyclesAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken);

    /// <summary>The cycle each of the rafts has open on the date (as <see cref="CycleOnDateAsync"/>), in one read; rafts with none are left out.</summary>
    Task<IReadOnlyDictionary<Guid, CycleSpan>> CyclesOnDateAsync(IReadOnlyCollection<Guid> raftIds, DateOnly date, CancellationToken cancellationToken);

    /// <summary>The earliest start date among the Open cycles of each zone that has one (GFR-04); other zones are left out.</summary>
    Task<IReadOnlyDictionary<Guid, DateOnly>> EarliestOpenStartByZoneAsync(CancellationToken cancellationToken);
}
