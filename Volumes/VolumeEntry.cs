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
    /// <summary>Raw dat_line_id from the DB (Single volumes). Null for Group-owned volumes.</summary>
    public required string? RawDatLineId     { get; init; }
    /// <summary>Owning group id (Group-owned volumes). Null for Single volumes.</summary>
    public          string? GroupId          { get; init; }
    /// <summary>Absolute path to the DAT-line SQLite DB (Single). Empty for Group-owned volumes.</summary>
    public required string  DbPath           { get; init; }
    public          int     ArtifactCount    { get; init; }

    /// <summary>True when this volume is Group-owned (no single leaf owner / no single DB path).</summary>
    public bool IsGroupOwned => GroupId is not null;

    /// <summary>
    /// True when Move Volume may run for this volume: it is built and physically materialized
    /// (LOCAL or ON DISK ⇒ <c>Status == "present"</c>), regardless of ownership. INIT (not built) and LOST
    /// volumes are excluded. Owner-agnostic — V-G4 ported the Move workflow to Group-owned volumes, so the
    /// action is no longer restricted to Single. Mirrors the states accepted by the Move handler.
    /// </summary>
    public bool CanMoveVolume => Status == "present";

    /// <summary>
    /// Owner-aware Verify Volume readiness. Both ownership kinds require at least one assigned artifact. A Single
    /// volume additionally requires its leaf DB to be available (<paramref name="singleDbAvailable"/> =
    /// <c>DbPath</c> exists) — the historical condition, preserved exactly. A Group volume has no single
    /// <c>DbPath</c>; its per-leaf DB / membership / root are validated at execution time, so the button only
    /// requires assigned artifacts. Pure — the caller supplies the filesystem probe result.
    /// </summary>
    public bool CanVerifyVolume(bool singleDbAvailable)
        => ArtifactCount > 0 && (IsGroupOwned || singleDbAvailable);

    /// <summary>
    /// Append Volume readiness: the volume must be built and physically materialized (LOCAL or ON DISK ⇒
    /// <c>Status == "present"</c>), regardless of ownership. Owner-agnostic — V-G6 ported Append to Group. The
    /// historical Single gate was <c>isPhysical &amp;&amp; isSingle</c>; for a Single owner <c>isSingle</c> is
    /// always true, so this preserves the Single truth table exactly while additionally enabling Group. Leaf-DB
    /// availability is validated at execution time (no <c>DbPath</c> requirement in the gate).
    /// </summary>
    public bool CanAppendVolume => Status == "present";

    /// <summary>
    /// Fillback readiness (owner-aware). Both kinds require a present volume with assigned artifacts. A Single
    /// volume additionally requires its leaf DB (<paramref name="singleDbAvailable"/>) — the historical condition,
    /// preserved exactly (for a Single owner <c>IsGroupOwned</c> is false, so this reduces to present + artifacts +
    /// DB). A Group volume has no single <c>DbPath</c>; its leaf DBs / membership / target are validated when
    /// Fillback opens. Pure — the caller supplies the filesystem probe result.
    /// </summary>
    public bool CanFillbackVolume(bool singleDbAvailable)
        => Status == "present" && ArtifactCount > 0 && (IsGroupOwned || singleDbAvailable);

    public double FillRatio => PlannedSizeBytes > 0
        ? System.Math.Clamp((double)ActualSizeBytes / PlannedSizeBytes, 0, 1)
        : 0;

    public string PlannedLabel => FormatBytes(PlannedSizeBytes);
    public string ActualLabel  => FormatBytes(ActualSizeBytes);

    public string StatusLabel =>
        Status == "init"                          ? "INIT"    :
        Status == "lost"                          ? "LOST"    :
        Health == "crit"                          ? "WARNING" :
        CurrentLocation.StartsWith("disk:")       ? "ON DISK" :
        CurrentLocation == "Local Archive"        ? "LOCAL"   :
                                                    "WARNING";

    public IBrush StatusBrush => StatusLabel switch
    {
        "INIT"    => new SolidColorBrush(Color.Parse("#78909C")),
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
