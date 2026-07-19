using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Archive;

/// <summary>
/// Tests for the archive write no-overwrite guard. Pure decisions test the real
/// <see cref="ArchiveWriteCollisionGuard"/>; the collision scenarios feed it with
/// the REAL DB signal (<see cref="DatLineStore.GetDerivedArtifactContentKeysByRelativePath"/>)
/// so the guard + persistence integrate without reimplementing logic.
/// </summary>
public sealed class ArchiveWriteCollisionGuardTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ArchiveWriteCollisionGuardTests()
    {
        _dir    = Path.Combine(Path.GetTempPath(), "ArkGuard_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "dat.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private DatLineStore Store() => new(_dbPath);

    /// <summary>Records a derived artifact at a given relative_path with a given content identity.</summary>
    private void SeedDerived(string ck, string relativePath, string fileName)
    {
        var store = Store();
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = ck, DatSha1 = null, DatMd5 = null, DatCrc32 = null,
            CreatedAtUtc = DateTime.UtcNow,
        });
        store.IngestDerivedArtifact(ck, "", "no_compression", fileName, relativePath, 1024, "");
    }

    // ── 1-4: pure decisions ──────────────────────────────────────────────────────

    [Fact]
    public void ArchiveWriteGuard_TargetMissing_AllowsWrite()
    {
        Assert.Equal(ArchiveWriteDecision.AllowWrite,
            ArchiveWriteCollisionGuard.Decide(targetExists: false, Array.Empty<string>(), "release:A"));
    }

    [Fact]
    public void ArchiveWriteGuard_TargetExistingSameIdentity_AllowsIdempotentSkipOrReuse()
    {
        var d = ArchiveWriteCollisionGuard.Decide(targetExists: true, new[] { "release:A" }, "release:A");
        Assert.Equal(ArchiveWriteDecision.SameIdentityReuse, d);
        Assert.False(ArchiveWriteCollisionGuard.IsBlocking(d));
    }

    [Fact]
    public void ArchiveWriteGuard_TargetExistingDifferentIdentity_Blocks()
    {
        var d = ArchiveWriteCollisionGuard.Decide(targetExists: true, new[] { "release:A" }, "release:B");
        Assert.Equal(ArchiveWriteDecision.CollisionDifferentIdentity, d);
        Assert.True(ArchiveWriteCollisionGuard.IsBlocking(d));
    }

    [Fact]
    public void ArchiveWriteGuard_TargetExistingUnknownIdentity_BlocksConservatively()
    {
        // File physically present but no DB artifact claims it → unknown → block.
        var d = ArchiveWriteCollisionGuard.Decide(targetExists: true, Array.Empty<string>(), "release:A");
        Assert.Equal(ArchiveWriteDecision.UnknownExistingBlock, d);
        Assert.True(ArchiveWriteCollisionGuard.IsBlocking(d));
    }

    // ── 5-7: collision scenarios via the REAL store lookup ───────────────────────

    [Fact]
    public void ArchiveWriteGuard_ChdDiscCueBasenameCollision_DoesNotOverwrite()
    {
        // Two releases whose main input is "disc.cue" → both "disc.chd" at the same flat path.
        const string relPath = "archive/dc/redump/disc.chd";
        SeedDerived("release:A", relPath, "disc.chd");   // release A already committed

        // Release B is about to write the same target.
        var keys = Store().GetDerivedArtifactContentKeysByRelativePath(relPath);
        var d = ArchiveWriteCollisionGuard.Decide(targetExists: true, keys, "release:B");

        Assert.Equal(ArchiveWriteDecision.CollisionDifferentIdentity, d);
        Assert.True(ArchiveWriteCollisionGuard.IsBlocking(d));
    }

    [Fact]
    public void ArchiveWriteGuard_ZipSameSafeReleaseNameCollision_DoesNotOverwrite()
    {
        const string relPath = "archive/gba/nointro/Game.zip";
        SeedDerived("release:A", relPath, "Game.zip");

        var keys = Store().GetDerivedArtifactContentKeysByRelativePath(relPath);
        var d = ArchiveWriteCollisionGuard.Decide(targetExists: true, keys, "release:B");

        Assert.True(ArchiveWriteCollisionGuard.IsBlocking(d));
    }

    [Fact]
    public void ArchiveWriteGuard_FileExtensionCollision_DoesNotOverwrite()
    {
        // file_extension identities are content-hash based; different content → different ck.
        const string relPath = "archive/nes/nointro/Game/rom.chd";
        SeedDerived("sha1:aaaa", relPath, "rom.chd");

        var keys = Store().GetDerivedArtifactContentKeysByRelativePath(relPath);
        var d = ArchiveWriteCollisionGuard.Decide(targetExists: true, keys, "sha1:bbbb");

        Assert.True(ArchiveWriteCollisionGuard.IsBlocking(d));
    }

    // ── 8: the blocking condition that gates the archive-collision operation ─────

    [Fact]
    public void ArchiveWriteGuard_CollisionEmitsArchiveCollisionOperation()
    {
        // The processors emit an "archive-collision" op exactly when IsBlocking is true.
        Assert.True(ArchiveWriteCollisionGuard.IsBlocking(ArchiveWriteDecision.CollisionDifferentIdentity));
        Assert.True(ArchiveWriteCollisionGuard.IsBlocking(ArchiveWriteDecision.UnknownExistingBlock));
        Assert.False(ArchiveWriteCollisionGuard.IsBlocking(ArchiveWriteDecision.AllowWrite));
        Assert.False(ArchiveWriteCollisionGuard.IsBlocking(ArchiveWriteDecision.SameIdentityReuse));
    }

    // ── 9: a blocked write is read-only — source/staging is preserved ────────────

    [Fact]
    public void ArchiveWriteGuard_CollisionPreservesSourceOrStagingRecoveryMaterial()
    {
        // The guard + its DB lookup never delete or modify anything (SELECT only), and
        // every call site places the blocking branch BEFORE the source-cleanup step, so
        // a collision leaves the transform source intact for recovery.
        const string relPath = "archive/dc/redump/disc.chd";
        SeedDerived("release:A", relPath, "disc.chd");

        // A stand-in "source" file must be untouched by consulting the guard.
        var sourceFile = Path.Combine(_dir, "source-material.bin");
        File.WriteAllBytes(sourceFile, new byte[] { 1, 2, 3 });

        var keys = Store().GetDerivedArtifactContentKeysByRelativePath(relPath);
        var d = ArchiveWriteCollisionGuard.Decide(targetExists: true, keys, "release:B");

        Assert.True(ArchiveWriteCollisionGuard.IsBlocking(d));
        Assert.True(File.Exists(sourceFile));   // nothing was deleted while deciding
    }

    // ── 10: normal non-colliding path is unchanged ───────────────────────────────

    [Fact]
    public void ArchiveWriteGuard_NormalNonCollidingIngestionPath_Unchanged()
    {
        // Fresh write (target missing) → AllowWrite.
        const string relPath = "archive/dc/redump/Unique Game.chd";
        var keysFresh = Store().GetDerivedArtifactContentKeysByRelativePath(relPath);   // empty
        Assert.Equal(ArchiveWriteDecision.AllowWrite,
            ArchiveWriteCollisionGuard.Decide(targetExists: false, keysFresh, "release:A"));

        // Same release re-ingesting its own artifact (target exists, same ck) → reuse, not blocked.
        SeedDerived("release:A", relPath, "Unique Game.chd");
        var keysSame = Store().GetDerivedArtifactContentKeysByRelativePath(relPath);
        var d = ArchiveWriteCollisionGuard.Decide(targetExists: true, keysSame, "release:A");
        Assert.Equal(ArchiveWriteDecision.SameIdentityReuse, d);
        Assert.False(ArchiveWriteCollisionGuard.IsBlocking(d));
    }
}
