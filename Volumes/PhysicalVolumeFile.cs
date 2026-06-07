namespace Arkadia.Volumes;

/// <summary>
/// Classification assigned to every physical file found during a full-scan Verify Volume.
/// </summary>
public enum VolumeFileClass
{
    Pending,           // not yet classified
    SystemFile,        // ARKADIA.DISK.json — leave in place
    OkWanted,          // expected active artifact at correct flat path, hash OK
    MisplacedWanted,   // expected active artifact found at wrong path, hash OK
    UnwantedFound,     // artifact whose release status is 'unwanted'
    KnownUnexpected,   // known Arkadia artifact that belongs to a different volume
    UnknownFile,       // hash matches nothing in any Arkadia DAT-line DB
}

/// <summary>
/// Represents a single physical file found during a recursive volume scan.
/// </summary>
public sealed class PhysicalVolumeFile
{
    public required string FullPath          { get; init; }
    /// <summary>Path relative to the volume root, e.g. "subfolder\Game.chd".</summary>
    public required string RelativePath      { get; init; }
    public required string FileName          { get; init; }
    public required long   SizeBytes         { get; init; }
    /// <summary>True when the file is directly inside the volume root (no subfolder).</summary>
    public required bool   IsInRoot          { get; init; }
    /// <summary>
    /// True when the file is under a managed folder (unwanted\, known\, unknown\).
    /// Files in managed folders are NOT classified as active volume content.
    /// </summary>
    public required bool   IsInManagedFolder { get; init; }

    public string          Sha1              { get; set; } = "";
    public VolumeFileClass Classification   { get; set; } = VolumeFileClass.Pending;

    // ── Populated for OkWanted / MisplacedWanted / UnwantedFound ────────────
    public string?         DerivedArtifactId   { get; set; }
    public string?         VolumeArtifactId    { get; set; }
    public long            ArtifactSizeBytes   { get; set; }
    /// <summary>Expected flat filename (FileName in derived_artifacts). Used for MISPLACED.</summary>
    public string?         CanonicalFileName   { get; set; }

    // ── Populated for KnownUnexpected ────────────────────────────────────────
    public string?         ExpectedVolumeId    { get; set; }
    public string?         ExpectedVolumeLabel { get; set; }
}
