using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class LaneIncidentRepository(OmtsDbContext db) : ILaneIncidentRepository
{
    public Task<LaneIncident?> GetForEditAsync(Guid incidentId, CancellationToken cancellationToken) =>
        db.LaneIncidents.Include(i => i.Lane).Include(i => i.Photos).Include(i => i.Materials).FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);

    public async Task<IReadOnlyList<LaneIncident>> ListOfRaftAsync(Guid raftId, Guid? laneId, CancellationToken cancellationToken) =>
        await db.LaneIncidents.AsNoTracking().Include(i => i.Lane).Include(i => i.Photos).Include(i => i.Materials)
            .Where(i => i.Lane.RaftId == raftId && (laneId == null || i.LaneId == laneId))
            .OrderByDescending(i => i.OccurredAt).ThenByDescending(i => i.Kind)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LaneIncident>> LossEventsOfCycleAsync(Guid cycleId, CancellationToken cancellationToken) =>
        await db.LaneIncidents.AsNoTracking().Include(i => i.Lane)
            .Where(i => i.CycleId == cycleId && i.Kind == LaneIncidentKind.OutOfService && i.HadSeed)
            .OrderBy(i => i.Lane.LaneNumber)
            .ToListAsync(cancellationToken);

    public Task<LaneIncident?> OpenDamageOfLaneAsync(Guid laneId, CancellationToken cancellationToken) =>
        db.LaneIncidents
            .Where(i => i.LaneId == laneId && i.Kind == LaneIncidentKind.Damaged && !db.LaneIncidents.Any(r => r.ResolvesIncidentId == i.Id))
            .OrderByDescending(i => i.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(LaneIncident incident) => db.LaneIncidents.Add(incident);

    public void AddPhoto(LaneIncidentPhoto photo) => db.LaneIncidentPhotos.Add(photo);

    public void AddMaterial(LaneIncidentMaterial material) => db.LaneIncidentMaterials.Add(material);
}
