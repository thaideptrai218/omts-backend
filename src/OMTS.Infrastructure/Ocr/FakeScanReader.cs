using System.Globalization;
using OMTS.Application.Features.Farming.Rafts.FormScan;
using OMTS.Application.Features.Farming.Zones.BoundaryScan;
using OMTS.Application.Features.Scans;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// Scan reader for Development and tests (Ocr:Provider=Fake): answers one fixed made-up licence map (four zones of 12.5, 6.2, 5.8 and 4 ha, 28.5 ha in all, in Vân Đồn)
/// and one fixed raft form whatever the photo shows, so the app's scan screens can be tried without a Gemini key. Refused
/// outside Development by the startup check, like <see cref="FakeOcrService"/>.
/// </summary>
internal sealed class FakeScanReader : IZoneMapReader, IRaftFormReader
{
    Task<ScanRead<RawZoneMap>> IZoneMapReader.ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        Task.FromResult(ScanRead<RawZoneMap>.Read(new RawZoneMap(
            "VN-2000",
            "107°45'",
            [
                Rectangle("Khu 1", "12,50", 1, 438_000, 250, 500),
                Rectangle("Khu 2", "6,20", 5, 438_600, 200, 310),
                Rectangle("Khu 3", "5,80", 9, 439_000, 200, 290),
                Rectangle("Khu 4", "4,00", 13, 439_400, 200, 200),
            ],
            "Xã Đông Xá, xã Bản Sen, huyện Vân Đồn, tỉnh Quảng Ninh",
            "28,50 ha")));

    /// <summary>A rectangle of the given metres whose south-west corner is at northing 2.315.000, so its grid area is the written one.</summary>
    private static RawMapZone Rectangle(string name, string writtenHectares, int firstCorner, int easting, int northMetres, int eastMetres)
    {
        const int northing = 2_315_000;
        static string Grid(int metres) => metres.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.') + ",00";
        return new RawMapZone(name, $"(Diện tích: {writtenHectares} ha)",
        [
            new RawMapCorner($"K{firstCorner}", Grid(northing), Grid(easting), null, null),
            new RawMapCorner($"K{firstCorner + 1}", Grid(northing), Grid(easting + eastMetres), null, null),
            new RawMapCorner($"K{firstCorner + 2}", Grid(northing + northMetres), Grid(easting + eastMetres), null, null),
            new RawMapCorner($"K{firstCorner + 3}", Grid(northing + northMetres), Grid(easting), null, null),
        ]);
    }

    Task<ScanRead<RaftFormReading>> IRaftFormReader.ReadAsync(ReadOnlyMemory<byte> photo, string mimeType, CancellationToken cancellationToken) =>
        Task.FromResult(ScanRead<RaftFormReading>.Read(new RaftFormReading("B-01", "24 m x 10 m", "6", "Gần cửa sông", "Bè mới", "10.1234", "106.5678")));
}
