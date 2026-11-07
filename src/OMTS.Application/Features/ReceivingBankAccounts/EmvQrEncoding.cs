using System.Globalization;
using System.Text;

namespace OMTS.Application.Features.ReceivingBankAccounts;

/// <summary>The EMVCo QR building blocks: data objects (id, two-digit length, value) and the closing CRC.</summary>
public static class EmvQrEncoding
{
    private const string CrcIdAndLength = "6304";

    /// <summary>One data object. The value must be ASCII, so that its length in characters is also its length in bytes.</summary>
    public static string Field(string id, string value)
    {
        if (id.Length != 2 || value.Length > 99 || !Ascii.IsValid(value))
        {
            throw new ArgumentException($"Data object {id} cannot be encoded.", nameof(value));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{id}{value.Length:00}{value}");
    }

    /// <summary>Appends object 63: the CRC covers everything before it, including its own id and length.</summary>
    public static string WithCrc(string payload)
    {
        var covered = payload + CrcIdAndLength;
        return covered + Crc16Ccitt(covered).ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>CRC-16/CCITT-FALSE: polynomial 0x1021, initial value 0xFFFF, over the ASCII bytes.</summary>
    public static ushort Crc16Ccitt(string text)
    {
        var crc = 0xFFFF;
        foreach (var value in Encoding.ASCII.GetBytes(text))
        {
            crc ^= value << 8;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
                crc &= 0xFFFF;
            }
        }

        return (ushort)crc;
    }
}
