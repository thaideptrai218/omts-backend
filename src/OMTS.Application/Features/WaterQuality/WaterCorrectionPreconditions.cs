using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// What WATER-02 asks before anything changes (PRE-2, BR-WATER-06, GBR-01): a Zone Manager changes only what they recorded,
/// in a zone they can still access, the Cooperative Owner any measurement, and only an Active measurement of an In Use zone.
/// </summary>
public sealed class WaterCorrectionPreconditions(
    IWaterMeasurementRepository measurements, ZoneAccessGuard zoneAccess, IStatusCatalog statusCatalog)
{
    /// <summary>Object name used in MSG-GEN-09.</summary>
    private const string ZoneObjectName = "farming zone";

    /// <summary>The measurement (tracked, with its zone and raft), or why the caller cannot change it: who may act comes first.</summary>
    public async Task<Result<WaterMeasurement>> LoadChangeableAsync(Guid id, Guid callerId, CancellationToken cancellationToken)
    {
        var measurement = await measurements.GetAsync(id, cancellationToken);
        if (measurement is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.WaterMeasurements, PermissionAction.Edit, measurement.ZoneId, cancellationToken)
            || (measurement.MeasuredByAccountId != callerId && !await zoneAccess.IsCooperativeOwnerAsync(cancellationToken)))
        {
            return AppError.Forbidden;
        }

        if (measurement.StatusId != StatusIds.WaterMeasurement.Active)
        {
            return WaterMeasurementStatuses.CannotChange(statusCatalog, measurement.StatusId);
        }

        // GBR-01: the measurements of a Discontinued zone can only be viewed.
        return measurement.Zone.StatusId == StatusIds.Usage.InUse
            ? measurement
            : AppError.Conflict(UserMessages.WrongStatus(ZoneObjectName, statusCatalog.DisplayNameOf(measurement.Zone.StatusId)));
    }

    /// <summary>
    /// The raft the corrected measurement names, or null when the request cannot name it: another raft must be In Use in
    /// the same zone (BR-WATER-01); the raft it already has stays allowed after that raft was discontinued, as it only
    /// records where the water was measured.
    /// </summary>
    public async Task<Raft?> RaftOfCorrectionAsync(WaterMeasurement measurement, Guid? raftId, CancellationToken cancellationToken)
    {
        if (raftId == measurement.RaftId)
        {
            return measurement.Raft;
        }

        var raft = raftId is { } id ? await measurements.GetRaftAsync(id, cancellationToken) : null;
        return raft is not null && raft.ZoneId == measurement.ZoneId && raft.StatusId == StatusIds.Usage.InUse ? raft : null;
    }
}
