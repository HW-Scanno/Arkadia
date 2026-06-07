using System.Collections.Generic;

namespace Arkadia.Volumes;

/// <summary>
/// Result of a full-scan Verify Volume operation performed by
/// <see cref="VolumeVerifyService"/>.
/// </summary>
public sealed class VolumeVerifyResult
{
    /// <summary>Total physical files found (including managed-folder files).</summary>
    public int TotalScanned         { get; init; }
    /// <summary>Arkadia system files (ARKADIA.DISK.json etc.) — left in place.</summary>
    public int SystemFiles          { get; init; }
    /// <summary>Expected active artifacts verified at the correct flat root path.</summary>
    public int Verified             { get; init; }
    public int MisplacedFound       { get; init; }
    public int MisplacedRestored    { get; init; }
    /// <summary>Misplaced files that could not be restored due to a target collision.</summary>
    public int MisplacedCollisions  { get; init; }
    public int UnwantedFound        { get; init; }
    public int UnwantedMoved        { get; init; }
    public int KnownUnexpectedFound { get; init; }
    public int KnownUnexpectedMoved { get; init; }
    public int UnknownFound         { get; init; }
    public int UnknownMoved         { get; init; }
    /// <summary>Expected active artifacts that could not be found anywhere in the active area.</summary>
    public int Missing              { get; init; }
    /// <summary>Errors (collisions, I/O failures) that prevent full recovery.</summary>
    public int Errors               { get; init; }

    /// <summary>
    /// True only when every expected active artifact is present at the flat root,
    /// hash-verified, and no unresolved files remain in the active area.
    /// </summary>
    public bool IsHealthy          { get; init; }

    /// <summary>True when at least one recovery move was performed.</summary>
    public bool HadRecoveryActions { get; init; }

    public IReadOnlyList<string> LogLines { get; init; } = [];
}
