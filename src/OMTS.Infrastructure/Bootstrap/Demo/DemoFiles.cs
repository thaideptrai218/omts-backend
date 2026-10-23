using System.IO.Compression;
using System.Text;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>Placeholder files for the demo data: a generated logo and a one-page license document.</summary>
internal static class DemoFiles
{
    private const int LogoSize = 64;

    /// <summary>A 64×64 PNG: a pale disc on blue, standing in for the cooperative logo.</summary>
    public static IncomingFile Logo()
    {
        var raw = new byte[LogoSize * (1 + LogoSize * 3)];
        var offset = 0;
        for (var y = 0; y < LogoSize; y++)
        {
            raw[offset++] = 0; // filter: none
            for (var x = 0; x < LogoSize; x++)
            {
                var inside = Math.Pow(x - 31.5, 2) + Math.Pow(y - 31.5, 2) <= 22 * 22;
                (raw[offset++], raw[offset++], raw[offset++]) = inside ? ((byte)0xF2, (byte)0xF4, (byte)0xF4) : ((byte)0x1B, (byte)0x4F, (byte)0x72);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteInt(header, 0, LogoSize);
        WriteInt(header, 4, LogoSize);
        (header[8], header[9]) = (8, 2); // 8 bits per channel, RGB
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        png.Position = 0;
        return new IncomingFile("logo-demo.png", png.Length, png);
    }

    /// <summary>A tiny PDF that passes the document check; the license facts live in the license record.</summary>
    public static IncomingFile LicenseDocument(string number)
    {
        var bytes = Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            $"3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 150]/Title({number})>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");
        return new IncomingFile($"{number}.pdf", bytes.Length, new MemoryStream(bytes));
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var crc = new byte[4];
        WriteInt(crc, 0, unchecked((int)Crc32(body)));
        output.Write(length);
        output.Write(body);
        output.Write(crc);
    }

    private static void WriteInt(byte[] target, int index, int value)
    {
        target[index] = (byte)(value >> 24);
        target[index + 1] = (byte)(value >> 16);
        target[index + 2] = (byte)(value >> 8);
        target[index + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
