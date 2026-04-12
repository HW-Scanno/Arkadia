using Avalonia.Media;

namespace Arkadia.Volumes;

/// <summary>View-model for a single row in the Volumes table.</summary>
public sealed class VolumeEntry
{
    public required string  Id               { get; init; }
    public required string  Label            { get; init; }
    public required string  PlatformId       { get; init; }
    public required string  DatLineId        { get; init; }
    public required string  Status           { get; init; }
    public required string  Health           { get; init; }  // ok | crit
    public required long    PlannedSizeBytes { get; init; }
    public required long    ActualSizeBytes  { get; init; }
    public required string  CurrentLocation  { get; init; }  // "disk:<label>" | "source" | "workspace" | "—"
    public required string? DiskId           { get; init; }
    public required string? DiskLabel        { get; init; }
    /// <summary>Raw dat_line_id from the DB (not the display name).</summary>
    public required string  RawDatLineId     { get; init; }
    /// <summary>Absolute path to the DAT-line SQLite DB for this volume.</summary>
    public required string  DbPath           { get; init; }
    public          int     ArtifactCount    { get; init; }

    public double FillRatio => PlannedSizeBytes > 0
        ? System.Math.Clamp((double)ActualSizeBytes / PlannedSizeBytes, 0, 1)
        : 0;

    public string PlannedLabel => FormatBytes(PlannedSizeBytes);
    public string ActualLabel  => FormatBytes(ActualSizeBytes);

    public string StatusLabel =>
        Status == "lost"                          ? "LOST"    :
        CurrentLocation.StartsWith("disk:")       ? "ON DISK" :
        CurrentLocation == "Local Archive"        ? "LOCAL"   :
                                                    "WARNING";

    public IBrush StatusBrush => StatusLabel switch
    {
        "ON DISK" => new SolidColorBrush(Color.Parse("#4CAF50")),
        "LOCAL"   => new SolidColorBrush(Color.Parse("#26C6DA")),
        "LOST"    => new SolidColorBrush(Color.Parse("#EF5350")),
        _         => new SolidColorBrush(Color.Parse("#FFB300")),  // WARNING
    };

    private static string FormatBytes(long b)
    {
        if (b <= 0)                    return "0 B";
        if (b < 1024L)                 return $"{b} B";
        if (b < 1024L * 1024)          return $"{b / 1024.0:F1} KB";
        if (b < 1024L * 1024 * 1024)   return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }
}
