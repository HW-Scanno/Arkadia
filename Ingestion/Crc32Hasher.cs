using System;
using System.IO;

namespace Arkadia.Ingestion;

/// <summary>
/// Streaming CRC-32 (ISO 3309 / IEEE 802.3, the ZIP/No-Intro/Redump polynomial), table-driven. Used only by the
/// CRC32+size fallback for CRC-only DAT requirements; SHA1/MD5 remain the primary identity.
/// </summary>
public static class Crc32Hasher
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC-32 of the stream as 8 lowercase hex digits.</summary>
    public static string ComputeHex(Stream stream)
    {
        uint crc = 0xFFFFFFFF;
        var buf = new byte[81920];
        int read;
        while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++)
                crc = Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        return (crc ^ 0xFFFFFFFF).ToString("x8");
    }

    public static string ComputeHex(string path)
    {
        using var fs = File.OpenRead(path);
        return ComputeHex(fs);
    }

    /// <summary>Normalizes a DAT crc to 8 lowercase hex digits; "" when blank or not a valid hex CRC.</summary>
    public static string Normalize(string? crc)
    {
        var t = (crc ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0 || t.Length > 8) return "";
        foreach (var ch in t)
            if (!Uri.IsHexDigit(ch)) return "";
        return t.PadLeft(8, '0');
    }
}
