namespace Arkadia;

/// <summary>View-model row for the AssignDerivedArtifactsDialog list.</summary>
public sealed class ArtifactRow
{
    public required string Id                  { get; init; }
    public required string FileName            { get; init; }
    public required long   Size                { get; init; }
    public required string ContentIdentityKey  { get; init; }

    public string SizeLabel => FormatBytes(Size);
    public string KeyShort  => ContentIdentityKey.Length > 16
        ? ContentIdentityKey[..16] + "…"
        : ContentIdentityKey;

    private static string FormatBytes(long b)
    {
        if (b <= 0)                  return "0 B";
        if (b < 1024L * 1024)        return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}
