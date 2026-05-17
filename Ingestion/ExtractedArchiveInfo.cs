using System.Collections.Generic;

namespace Arkadia.Ingestion;

/// <summary>
/// Tracks one successfully-extracted archive container and the full paths of every
/// file it produced.  Used by <see cref="ArchiveCleanupPlanner"/> to decide
/// per-archive whether cleanup is safe after the ingestion run.
/// </summary>
public sealed record ExtractedArchiveInfo(
    /// <summary>Full path of the archive file (e.g. incoming-roms\ps2\game.zip).</summary>
    string ArchivePath,
    /// <summary>Full path of the sibling extraction folder created for this archive.</summary>
    string ExtractionRoot,
    /// <summary>Full paths of every file extracted from the archive.</summary>
    IReadOnlyList<string> ExtractedFiles);
