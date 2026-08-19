namespace Arkadia.Systems;

/// <summary>
/// One read-only row in the Single DAT Details dialog: which volume stores this DAT's artifacts, how many, how
/// big, and on which disk. <see cref="SizeBytes"/> is the volume's authoritative <c>actual_size_bytes</c> (never
/// planned); <see cref="Size"/> is its formatted form. <see cref="Disk"/> is resolved from persisted catalog
/// location only.
/// </summary>
public sealed record SingleDatVolumeRow(string Volume, int Artifacts, long SizeBytes, string Size, string Disk);

/// <summary>
/// Pure display helpers for the Single DAT Details dialog. The Disk column answers "which disk does Arkadia say
/// this volume belongs on?" from persisted catalog state alone — no live disk discovery, no online/offline
/// annotation. Testable without a catalog.
/// </summary>
public static class SingleDatDetails
{
    public const string LocalLabel  = "LOCAL";
    public const string NoLocation  = "—";
    public const string UnknownDisk = "(unknown disk)";

    /// <summary>
    /// The Disk column value from a volume's current persisted location: <c>workspace</c> → LOCAL; <c>disk</c> →
    /// the persisted disk label (or a placeholder if the label is missing); no current location → em dash. Any
    /// other/unknown location type also falls back to the em dash.
    /// </summary>
    public static string DiskDisplay(string? locationType, string? diskLabel) => locationType switch
    {
        "workspace" => LocalLabel,
        "disk"      => string.IsNullOrEmpty(diskLabel) ? UnknownDisk : diskLabel!,
        _           => NoLocation,
    };
}
