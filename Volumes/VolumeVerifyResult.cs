using System.Collections.Generic;
using System.Linq;

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
    /// <summary>
    /// Stale volume_artifacts assignments removed because their file was confirmed
    /// missing from the reachable, fully-scanned volume (actual_size_bytes decremented).
    /// </summary>
    public int StaleAssignmentsRemoved { get; init; }
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

    /// <summary>
    /// Aggregates per-leaf results into one Group-volume result: integer counters SUM, <see cref="IsHealthy"/>
    /// is the AND of all leaves (a healthy-only-if-every-leaf-healthy rule; empty ⇒ healthy), and
    /// <see cref="HadRecoveryActions"/> is the OR. Log lines are concatenated in the order given. Used by the
    /// Group Verify orchestrator; the Single path never calls it.
    /// </summary>
    public static VolumeVerifyResult Combine(IReadOnlyList<VolumeVerifyResult> parts)
    {
        var log = new List<string>();
        foreach (var p in parts) log.AddRange(p.LogLines);

        return new VolumeVerifyResult
        {
            TotalScanned            = parts.Sum(p => p.TotalScanned),
            SystemFiles             = parts.Sum(p => p.SystemFiles),
            Verified                = parts.Sum(p => p.Verified),
            MisplacedFound          = parts.Sum(p => p.MisplacedFound),
            MisplacedRestored       = parts.Sum(p => p.MisplacedRestored),
            MisplacedCollisions     = parts.Sum(p => p.MisplacedCollisions),
            UnwantedFound           = parts.Sum(p => p.UnwantedFound),
            UnwantedMoved           = parts.Sum(p => p.UnwantedMoved),
            KnownUnexpectedFound    = parts.Sum(p => p.KnownUnexpectedFound),
            KnownUnexpectedMoved    = parts.Sum(p => p.KnownUnexpectedMoved),
            UnknownFound            = parts.Sum(p => p.UnknownFound),
            UnknownMoved            = parts.Sum(p => p.UnknownMoved),
            Missing                 = parts.Sum(p => p.Missing),
            StaleAssignmentsRemoved = parts.Sum(p => p.StaleAssignmentsRemoved),
            Errors                  = parts.Sum(p => p.Errors),
            IsHealthy               = parts.All(p => p.IsHealthy),
            HadRecoveryActions      = parts.Any(p => p.HadRecoveryActions),
            LogLines                = log,
        };
    }
}
