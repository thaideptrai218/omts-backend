using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Scans;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Farming.Zones.BoundaryScan;

/// <summary>
/// Reads the corner tables of the sea-area licence map photo into a suggested boundary (FARM-02). The photo goes to the vision
/// model, which only copies the tables; this service's <see cref="ZoneMapNormaliser"/> turns them into numbers and checks the
/// areas. Cooperative Owner only (API filter, like <c>PUT …/boundary</c>) and only for an In Use zone. Nothing is saved: the app
/// converts the grid coordinates, the Owner reviews them and saves with <c>PUT api/zones/{zoneId}/boundary</c>. A scan can also
/// start before the zone exists (the create-zone form): that scan has no zone and is found only on the zone-less route.
/// </summary>
public sealed class ZoneBoundaryScanService(IFarmingZoneRepository zones, StatusChangeService statusChanges, PhotoScanGateway scans)
{
    public async Task<Result<ScanStarted>> StartAsync(Guid zoneId, IncomingFile? photo, CancellationToken cancellationToken)
    {
        var zone = (await zones.GetByIdsAsync([zoneId], cancellationToken)).FirstOrDefault();
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return statusChanges.WrongStatus($"Farming zone {zone.Code}", zone);
        }

        return scans.Start(photo, ScopeOf(zoneId), ReadAsync);
    }

    /// <summary>Starts a scan with no zone, for the create-zone form; the permission (Cooperative Owner) is the API filter's.</summary>
    public Result<ScanStarted> StartWithoutZone(IncomingFile? photo) => scans.Start(photo, ZoneLessScope, ReadAsync);

    public Result<ScanStatusView<ZoneMapReading>> GetWithoutZone(Guid scanId) => scans.Get<ZoneMapReading>(scanId, ZoneLessScope);

    public Result<ScanStatusView<ZoneMapReading>> Get(Guid zoneId, Guid scanId) => scans.Get<ZoneMapReading>(scanId, ScopeOf(zoneId));

    /// <summary>Runs on a queue worker with its own service scope; never throws for a provider problem.</summary>
    private static async Task<ScanOutcome> ReadAsync(IServiceProvider services, ScannedPhoto photo, CancellationToken cancellationToken)
    {
        var read = await services.GetRequiredService<IZoneMapReader>().ReadAsync(photo.Bytes, photo.MimeType, cancellationToken);
        return read.Value is { } raw
            ? ScanOutcome.Done(ZoneMapNormaliser.Normalise(raw))
            : ScanOutcome.Failed(read.ErrorCode ?? OcrErrorCodes.BadResponse);
    }

    private const string ZoneLessScope = "boundary:none";

    private static string ScopeOf(Guid zoneId) => $"boundary:{zoneId:N}";
}
