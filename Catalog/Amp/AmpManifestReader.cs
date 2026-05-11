using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Arkadia;

public static class AmpManifestReader
{
    public static bool TryReadManifest(string ampFilePath, out AmpManifestInfo info)
    {
        info = Empty();
        try
        {
            if (!File.Exists(ampFilePath))
                return false;

            using var zip   = ZipFile.OpenRead(ampFilePath);
            var       entry = zip.GetEntry("manifest.json");
            if (entry is null)
                return false;

            using var stream = entry.Open();
            using var doc    = JsonDocument.Parse(stream);
            var       root   = doc.RootElement;

            if (!TryGetString(root, "FormatName",       out var formatName)      ||
                !TryGetString(root, "FormatVersion",    out var formatVersion)   ||
                !TryGetString(root, "HardwareFamilyId", out var hardwareFamilyId)||
                !TryGetString(root, "DatLineId",        out var datLineId)       ||
                !TryGetString(root, "SystemName",       out var systemName)      ||
                !TryGetInt   (root, "ReleaseCount",     out var releaseCount)    ||
                !TryGetInt   (root, "MediaFileCount",   out var mediaFileCount)  ||
                !TryGetLong  (root, "TotalMediaBytes",  out var totalMediaBytes) ||
                !TryGetInt   (root, "ExclusionCount",   out var exclusionCount)  ||
                !TryGetInt   (root, "ExtraNotesCount",  out var extraNotesCount))
                return false;

            string? attributionNotice         = null;
            string? attributionGeneralCredits = null;
            if (root.TryGetProperty("Attribution", out var attribution) &&
                attribution.ValueKind == JsonValueKind.Object)
            {
                if (attribution.TryGetProperty("Notice", out var n) &&
                    n.ValueKind == JsonValueKind.String)
                    attributionNotice = n.GetString();
                if (attribution.TryGetProperty("GeneralCredits", out var gc) &&
                    gc.ValueKind == JsonValueKind.String)
                    attributionGeneralCredits = gc.GetString();
            }

            info = new AmpManifestInfo(
                FormatName:               formatName,
                FormatVersion:            formatVersion,
                HardwareFamilyId:         hardwareFamilyId,
                DatLineId:                datLineId,
                SystemName:               systemName,
                ReleaseCount:             releaseCount,
                MediaFileCount:           mediaFileCount,
                TotalMediaBytes:          totalMediaBytes,
                ExclusionCount:           exclusionCount,
                ExtraNotesCount:          extraNotesCount,
                AttributionNotice:        attributionNotice,
                AttributionGeneralCredits: attributionGeneralCredits);
            return true;
        }
        catch
        {
            info = Empty();
            return false;
        }
    }

    private static AmpManifestInfo Empty() =>
        new("", "", "", "", "", 0, 0, 0L, 0, 0);

    private static bool TryGetString(JsonElement el, string prop, out string value)
    {
        value = "";
        if (!el.TryGetProperty(prop, out var p)) return false;
        if (p.ValueKind != JsonValueKind.String)  return false;
        value = p.GetString() ?? "";
        return true;
    }

    private static bool TryGetInt(JsonElement el, string prop, out int value)
    {
        value = 0;
        if (!el.TryGetProperty(prop, out var p)) return false;
        if (p.ValueKind != JsonValueKind.Number)  return false;
        return p.TryGetInt32(out value);
    }

    private static bool TryGetLong(JsonElement el, string prop, out long value)
    {
        value = 0L;
        if (!el.TryGetProperty(prop, out var p)) return false;
        if (p.ValueKind != JsonValueKind.Number)  return false;
        return p.TryGetInt64(out value);
    }
}
