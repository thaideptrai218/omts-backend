using OMTS.Application.Features.Scans;

namespace OMTS.Application.Features.Farming.Rafts.FormScan;

/// <summary>Reads the raft form photo into the fields as written; the implementation calls the vision model and never throws.</summary>
public interface IRaftFormReader
{
    Task<ScanRead<RaftFormReading>> ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken);
}
