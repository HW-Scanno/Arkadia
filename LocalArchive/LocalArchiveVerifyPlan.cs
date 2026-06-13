using System.Collections.Generic;
using System.Linq;

namespace Arkadia.LocalArchive;

/// <summary>
/// Result of a <see cref="LocalArchiveVerifyService.Verify"/> scan.
/// All entries represent physical files found on disk — the scan is filesystem-first.
/// </summary>
public sealed class LocalArchiveVerifyPlan
{
    public string PlatformId { get; init; } = "";
    public string DatLineId  { get; init; } = "";
    public string ArchiveDir { get; init; } = "";

    /// <summary>
    /// One entry per physical file found in the archive directory.
    /// Never contains ArchiveMissingFile entries.
    /// </summary>
    public IReadOnlyList<LocalArchiveEntry> Entries { get; init; } = [];

    /// <summary>
    /// Optional diagnostic: number of DB derived artifacts that have no matching physical file.
    /// This is NOT part of the primary scan results and does NOT affect IsClean.
    /// Use a separate "Archive Completeness" report if you need to surface this.
    /// </summary>
    public int AbsentFromArchiveCount { get; init; }

    // ── Counts — all derived from physical files found on disk ───────────────

    public int FilesScanned        => Entries.Count;
    public int WantedOk            => Entries.Count(e => e.Classification == LocalArchiveClass.WantedArchiveOk);
    public int UnwantedArtifacts   => Entries.Count(e => e.Classification == LocalArchiveClass.UnwantedArchiveArtifact);
    public int UnknownFiles        => Entries.Count(e => e.Classification == LocalArchiveClass.UnknownArchiveFile);
    public int HashMismatches      => Entries.Count(e => e.Classification == LocalArchiveClass.ArchiveHashMismatch);
    public int DuplicateCollisions => Entries.Count(e => e.Classification == LocalArchiveClass.ArchiveDuplicateCollision);
    public int RepairableCount     => Entries.Count(e => e.IsRepairable);

    public bool IsClean =>
        UnwantedArtifacts   == 0 &&
        UnknownFiles        == 0 &&
        HashMismatches      == 0 &&
        DuplicateCollisions == 0;
}

/// <summary>Result of executing a repair pass on the plan.</summary>
public sealed class LocalArchiveRepairResult
{
    public bool   Success       { get; init; }
    public int    MovedToSkip   { get; init; }
    public int    RemovedDbRows { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> Log { get; init; } = [];
}
