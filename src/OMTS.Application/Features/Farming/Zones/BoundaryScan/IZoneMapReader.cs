using OMTS.Application.Features.Scans;

namespace OMTS.Application.Features.Farming.Zones.BoundaryScan;

/// <summary>Reads the licence map photo into the raw texts (<see cref="RawZoneMap"/>); the implementation calls the vision model and never throws.</summary>
public interface IZoneMapReader
{
    Task<ScanRead<RawZoneMap>> ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken);
}
