using OMTS.Application.Features.Farming.Rafts.FormScan;
using OMTS.Application.Features.Farming.Zones.BoundaryScan;
using OMTS.Application.Features.Scans;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// Scan reader for Development and tests (Ocr:Provider=Fake): answers one fixed made-up licence map (one 100 m x 100 m zone of
/// 1 ha) and one fixed raft form whatever the photo shows, so the app's scan screens can be tried without a Gemini key. Refused
/// outside Development by the startup check, like <see cref="FakeOcrService"/>.
/// </summary>
internal sealed class FakeScanReader : IZoneMapReader, IRaftFormReader
{
    Task<ScanRead<RawZoneMap>> IZoneMapReader.ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        Task.FromResult(ScanRead<RawZoneMap>.Read(new RawZoneMap("VN-2000", "107°45'",
        [
            new RawMapZone("Khu 1", "(Diện tích: 1,00 ha)",
            [
                new RawMapCorner("K1", "2.315.000,00", "438.000,00", null, null),
                new RawMapCorner("K2", "2.315.000,00", "438.100,00", null, null),
                new RawMapCorner("K3", "2.315.100,00", "438.100,00", null, null),
                new RawMapCorner("K4", "2.315.100,00", "438.000,00", null, null),
            ]),
        ])));

    Task<ScanRead<RaftFormReading>> IRaftFormReader.ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        Task.FromResult(ScanRead<RaftFormReading>.Read(new RaftFormReading("B-01", "24 m x 10 m", "6", "Gần cửa sông", "Bè mới", "10.1234", "106.5678")));
}
