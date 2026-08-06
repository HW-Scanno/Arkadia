using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Arkadia.Data;

/// <summary>Coarse phase reported while a leaf DAT database is being persisted.</summary>
public enum LeafDatBuildPhase { SavingReleases, SavingFiles, Verifying }

/// <summary>Progress tick for <see cref="LeafDatDatabaseBuilder.Build"/> (1-based <see cref="Processed"/> of <see cref="Total"/>).</summary>
public sealed record LeafDatBuildProgress(LeafDatBuildPhase Phase, int Processed, int Total, string? CurrentItem = null);

/// <summary>Outcome of a successful leaf DAT database build (post-build verification counts included).</summary>
public sealed record LeafDatBuildResult(
    int ReleaseCount,
    int ReleaseFileCount,
    int VerifiedReleaseCount,
    int VerifiedReleaseFileCount);

/// <summary>The release files belonging to one prepared release (aligned to a <see cref="ReleaseRecord"/> id).</summary>
public sealed record LeafReleaseFileSet(string ReleaseId, IReadOnlyList<ReleaseFileRecord> Files);

/// <summary>
/// Fully-mapped, in-memory result of <see cref="LeafDatDatabaseBuilder.Prepare"/> — everything needed to
/// persist a leaf database, with no file created and no catalog/UI dependency. Produced BEFORE the
/// caller performs any catalog mutation, so no mapping exception can occur after the first catalog write.
/// </summary>
public sealed record LeafDatPreparedBuild
{
    public required string                        DatLineId        { get; init; }
    public required IReadOnlyList<ReleaseRecord>  Releases         { get; init; }
    public required IReadOnlyList<LeafReleaseFileSet> Files        { get; init; }
    public required int                           ReleaseFileCount { get; init; }
    public int ReleaseCount => Releases.Count;
}

/// <summary>
/// Materializes a single per-DAT-line SQLite database from already-parsed games, in two phases:
/// <see cref="Prepare"/> (pure, in-memory mapping — no file, no catalog) and <see cref="Build"/>
/// (persist the prepared data to a leaf DB). Splitting them lets the caller finish all mapping
/// <b>before</b> any catalog write, preserving the historical import ordering and not widening the
/// catalog↔leaf orphan window.
///
/// <para>Self-contained: depends only on the parser value types and <see cref="DatLineStore"/>, never on
/// the catalog DB, MainWindow, or Avalonia. <see cref="Build"/> writes ONLY the target leaf database at
/// the supplied path (final or a temporary <c>&lt;final&gt;.tmp-&lt;exec-id&gt;.db</c>) — it never touches
/// <c>dat_lines</c>, <c>dat_groups</c>, catalog working state, or any other file.</para>
///
/// <para>On return from <see cref="Build"/> the database is left <b>publishable</b>: the WAL is checkpointed
/// back into the main file and pooled connections are released, so the <c>.db</c> can be renamed with no
/// open handle and no dependency on a <c>-wal</c>/<c>-shm</c> sidecar. On cancellation/failure after the
/// file was created, connections are still released so the caller can delete the partial target — the
/// builder never deletes or modifies files it did not create.</para>
/// </summary>
public static class LeafDatDatabaseBuilder
{
    /// <summary>
    /// Pure, in-memory mapping of parsed games to release/file records (same id/status semantics as
    /// import). Creates no file, opens no database, and touches no catalog. Cancellable.
    /// </summary>
    public static LeafDatPreparedBuild Prepare(
        string                              datLineId,
        IReadOnlyList<DatParser.ParsedGame> games,
        CancellationToken                   cancellationToken)
    {
        if (datLineId is null) throw new ArgumentNullException(nameof(datLineId));
        if (games     is null) throw new ArgumentNullException(nameof(games));

        cancellationToken.ThrowIfCancellationRequested();

        var releases = new List<ReleaseRecord>(games.Count);
        var files    = new List<LeafReleaseFileSet>(games.Count);
        int fileCount = 0;

        for (int i = 0; i < games.Count; i++)
        {
            if ((i & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();   // during large mapping

            var game      = games[i];
            var releaseId = Guid.NewGuid().ToString("N");
            releases.Add(new ReleaseRecord
            {
                Id                = releaseId,
                DatLineId         = datLineId,
                Name              = game.Name,
                Status            = "missing",
                Region            = game.Region,
                Languages         = game.Languages,
                ReleaseContentKey = game.ContentKey,
            });

            var mapped = MapRoms(releaseId, game.Roms);
            fileCount += mapped.Count;
            files.Add(new LeafReleaseFileSet(releaseId, mapped));
        }

        return new LeafDatPreparedBuild
        {
            DatLineId        = datLineId,
            Releases         = releases,
            Files            = files,
            ReleaseFileCount = fileCount,
        };
    }

    /// <summary>Persists a prepared build to the leaf database at <paramref name="databasePath"/>.</summary>
    public static LeafDatBuildResult Build(
        string                           databasePath,
        LeafDatPreparedBuild             prepared,
        IProgress<LeafDatBuildProgress>? progress,
        CancellationToken                cancellationToken)
    {
        if (databasePath is null) throw new ArgumentNullException(nameof(databasePath));
        if (prepared     is null) throw new ArgumentNullException(nameof(prepared));

        cancellationToken.ThrowIfCancellationRequested();   // before any file is created

        // Prepare always produces concrete Lists; use them directly for the store API (no copy).
        var releases = prepared.Releases as List<ReleaseRecord> ?? prepared.Releases.ToList();

        DatLineStore? store = null;
        try
        {
            // Create/open the leaf DB (dir + schema created by the ctor).
            store = new DatLineStore(databasePath);

            progress?.Report(new LeafDatBuildProgress(LeafDatBuildPhase.SavingReleases, 0, releases.Count));
            store.SaveReleases(releases);

            for (int i = 0; i < prepared.Files.Count; i++)
            {
                if ((i & 0x3F) == 0) cancellationToken.ThrowIfCancellationRequested();   // between persistence batches
                var set   = prepared.Files[i];
                var files = set.Files as List<ReleaseFileRecord> ?? set.Files.ToList();
                store.SaveReleaseFiles(set.ReleaseId, files);

                if (i % 25 == 0 || i == prepared.Files.Count - 1)
                    progress?.Report(new LeafDatBuildProgress(
                        LeafDatBuildPhase.SavingFiles, i + 1, prepared.Files.Count, releases[i].Name));
            }

            // Post-build verification (reopens the DB; confirms openable + expected counts).
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new LeafDatBuildProgress(LeafDatBuildPhase.Verifying, 0, releases.Count));

            var verifiedReleases = store.LoadReleases().Count;
            var verifiedFiles    = store.LoadAllReleaseFiles().Values.Sum(v => v.Count);

            if (verifiedReleases != prepared.ReleaseCount)
                throw new InvalidOperationException(
                    $"Leaf DB verification failed: expected {prepared.ReleaseCount} releases, found {verifiedReleases}.");
            if (verifiedFiles != prepared.ReleaseFileCount)
                throw new InvalidOperationException(
                    $"Leaf DB verification failed: expected {prepared.ReleaseFileCount} release files, found {verifiedFiles}.");

            // Fold WAL into the main file and release pooled handles → publishable by rename.
            store.ConsolidateForPublish();

            return new LeafDatBuildResult(prepared.ReleaseCount, prepared.ReleaseFileCount, verifiedReleases, verifiedFiles);
        }
        finally
        {
            // On any exit after the file was created, drop pooled connections so the caller can
            // rename (success) or delete (cancel/failure) the target. Never deletes the file here.
            store?.ReleaseConnections();
        }
    }

    /// <summary>Convenience overload: <see cref="Prepare"/> then <see cref="Build"/> in one call.</summary>
    public static LeafDatBuildResult Build(
        string                              databasePath,
        string                              datLineId,
        IReadOnlyList<DatParser.ParsedGame> games,
        IProgress<LeafDatBuildProgress>?    progress,
        CancellationToken                   cancellationToken)
        => Build(databasePath, Prepare(datLineId, games, cancellationToken), progress, cancellationToken);

    private static List<ReleaseFileRecord> MapRoms(string releaseId, List<DatParser.ParsedRom> roms)
    {
        var result = new List<ReleaseFileRecord>(roms.Count);
        foreach (var rom in roms)
            result.Add(new ReleaseFileRecord
            {
                Id        = Guid.NewGuid().ToString("N"),
                ReleaseId = releaseId,
                RomName   = rom.Name,
                Size      = rom.Size,
                Crc       = rom.Crc,
                Md5       = rom.Md5,
                Sha1      = rom.Sha1,
            });
        return result;
    }
}
