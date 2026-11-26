using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Features.Farming.Rafts.FormScan;
using OMTS.Application.Features.Farming.Zones.BoundaryScan;
using OMTS.Application.Features.Scans;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// The scan reader when no real provider is configured (Ocr:Provider unset): every scan ends Failed/NOT_CONFIGURED, so the
/// app tells the user to type the data by hand. Like <see cref="NotConfiguredOcrService"/>, it never invents a reading.
/// </summary>
internal sealed class NotConfiguredScanReader : IZoneMapReader, IRaftFormReader
{
    Task<ScanRead<RawZoneMap>> IZoneMapReader.ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        Task.FromResult(ScanRead<RawZoneMap>.Failed(OcrErrorCodes.NotConfigured));

    Task<ScanRead<RaftFormReading>> IRaftFormReader.ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        Task.FromResult(ScanRead<RaftFormReading>.Failed(OcrErrorCodes.NotConfigured));
}
