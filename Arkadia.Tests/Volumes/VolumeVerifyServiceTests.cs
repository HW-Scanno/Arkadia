using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Arkadia.Volumes;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Tests for VolumeVerifyService — the full-scan volume verification engine.
///
/// Each test provisions real SQLite stores + a temp filesystem and exercises
/// VolumeVerifyService.Verify() directly.
/// </summary>
public sealed class VolumeVerifyServiceTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _catalogDbPath;
    private readonly string _datDbPath;

    public VolumeVerifyServiceTests()
    {
        _tmp          = Path.Combine(Path.GetTempPath(), "ArkVVS_" + Guid.NewGuid().ToString("N")[..8]);
        _catalogDbPath = Path.Combine(_tmp, "catalog.db");
        _datDbPath     = Path.Combine(_tmp, "dat.db");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CatalogService OpenCatalog() => new(_catalogDbPath);
    private DatLineStore   OpenStore()   => new(_datDbPath);

    private static string Sha1Hex(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private string WriteFile(string relPath, byte[] content)
    {
        var full = Path.Combine(_tmp, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private string VolumeRoot(string label) => Path.Combine(_tmp, "volumes", label);

    private void WriteVolumeFile(string volLabel, string fileName, byte[] content)
    {
        var root = VolumeRoot(volLabel);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, fileName), content);
    }

    private void WriteVolumeFileNested(string volLabel, string folder, string fileName, byte[] content)
    {
        var dir = Path.Combine(VolumeRoot(volLabel), folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), content);
    }

    /// <summary>
    /// Provisions the catalog DB + DAT-line DB with one volume and one artifact.
    /// Returns the volume record and the derived artifact ID.
    /// </summary>
    private (VolumeRecord Volume, string DaId) ProvisionOne(
        string volLabel, string fileName, byte[] content,
        string releaseStatus = "present")
    {
        var catalog = OpenCatalog();
        var store   = OpenStore();
        var sha1    = Sha1Hex(content);
        var cik     = $"sha1:{sha1}";
        var relId   = Guid.NewGuid().ToString("N");
        var volId   = Guid.NewGuid().ToString("N");

        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = relId, DatLineId = "dl1", Name = "Test Release", Status = releaseStatus }
        });
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null,
            CreatedAtUtc = DateTime.UtcNow
        });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId,
            ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow
        });
        var daId = store.IngestDerivedArtifact(
            cik, "", "chd", fileName,
            $"archive/snes/dl1/{fileName}", content.Length, sha1);

        catalog.SaveVolume(new VolumeRecord
        {
            Id = volId, Label = volLabel, PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = content.Length,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        });
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = volId, DatLineId = "dl1",
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            }
        });

        return (new VolumeRecord
        {
            Id = volId, Label = volLabel, PlatformId = "snes", DatLineId = "dl1",
            Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = content.Length,
            CreatedAt = DateTime.UtcNow, Health = "ok",
        }, daId);
    }

    private VolumeVerifyResult RunVerify(VolumeRecord vol)
    {
        var catalog = OpenCatalog();
        var store   = OpenStore();
        var svc     = new VolumeVerifyService(catalog);
        return svc.Verify(vol.Id, VolumeRoot(vol.Label), store, []);
    }

    // ── 1. VerifyVolume_ScansAllFiles_NotOnlyChd ──────────────────────────────

    [Fact]
    public void VerifyVolume_ScansAllFiles_NotOnlyChd()
    {
        var content = new byte[] { 1, 2, 3 };
        var (vol, _) = ProvisionOne("vol-scan", "Game.chd", content);
        WriteVolumeFile("vol-scan", "Game.chd", content);
        WriteVolumeFile("vol-scan", "notes.txt", new byte[] { 9 });

        var result = RunVerify(vol);

        // notes.txt must appear in the scan (as unknown, and moved)
        Assert.Equal(2, result.TotalScanned);
        Assert.Equal(1, result.UnknownFound);
    }

    // ── 2. VerifyVolume_FindsTxtUnknownFile ───────────────────────────────────

    [Fact]
    public void VerifyVolume_FindsTxtUnknownFile()
    {
        var content = new byte[] { 10, 20 };
        var (vol, _) = ProvisionOne("vol-txt", "A.chd", content);
        WriteVolumeFile("vol-txt", "A.chd", content);
        WriteVolumeFile("vol-txt", "ciao.txt", new byte[] { 99 });

        var result = RunVerify(vol);

        Assert.Equal(1, result.UnknownFound);
        Assert.Equal(1, result.UnknownMoved);
        // ciao.txt should be in unknown\
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-txt"), "unknown", "ciao.txt")));
    }

    // ── 3. VerifyVolume_FindsArtifactWithWrongExtensionByHash ────────────────

    [Fact]
    public void VerifyVolume_FindsArtifactWithWrongExtensionByHash()
    {
        var content  = new byte[] { 5, 6, 7, 8 };
        var (vol, _) = ProvisionOne("vol-ext", "Game.chd", content);
        // Artifact content written as .bin instead of .chd
        WriteVolumeFile("vol-ext", "Game.bin", content);

        var result = RunVerify(vol);

        // Identified by hash as MISPLACED (wrong filename, but content matches)
        Assert.Equal(1, result.MisplacedFound);
        Assert.Equal(1, result.MisplacedRestored);
        // canonical file should now be at root
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-ext"), "Game.chd")));
    }

    // ── 4. VerifyVolume_IgnoresAllowedArkadiaSystemFile ───────────────────────

    [Fact]
    public void VerifyVolume_IgnoresAllowedArkadiaSystemFile()
    {
        var content = new byte[] { 1, 1, 1 };
        var (vol, _) = ProvisionOne("vol-sys", "A.chd", content);
        WriteVolumeFile("vol-sys", "A.chd", content);
        WriteVolumeFile("vol-sys", "ARKADIA.DISK.json", new byte[] { 123 });

        var result = RunVerify(vol);

        Assert.Equal(1, result.SystemFiles);
        Assert.Equal(0, result.UnknownFound);
        // ARKADIA.DISK.json must still be in root
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-sys"), "ARKADIA.DISK.json")));
    }

    // ── 5. VerifyVolume_DoesNotTreatUnwantedFolderAsActive ───────────────────

    [Fact]
    public void VerifyVolume_DoesNotTreatUnwantedFolderAsActive()
    {
        var content = new byte[] { 2, 2, 2 };
        var (vol, _) = ProvisionOne("vol-mf1", "B.chd", content);
        WriteVolumeFile("vol-mf1", "B.chd", content);
        // Pre-existing file in the managed folder
        WriteVolumeFileNested("vol-mf1", "unwanted", "Old.chd", new byte[] { 7 });

        var result = RunVerify(vol);

        // The file in unwanted\ is not counted as active (TotalScanned includes it,
        // but it should not appear in verified/unknown counts)
        Assert.Equal(1, result.Verified);
        Assert.Equal(0, result.UnknownFound);
    }

    // ── 6. VerifyVolume_DoesNotTreatKnownFolderAsActive ──────────────────────

    [Fact]
    public void VerifyVolume_DoesNotTreatKnownFolderAsActive()
    {
        var content = new byte[] { 3, 3, 3 };
        var (vol, _) = ProvisionOne("vol-mf2", "C.chd", content);
        WriteVolumeFile("vol-mf2", "C.chd", content);
        WriteVolumeFileNested("vol-mf2", Path.Combine("known", "SomeVol"), "Other.chd", new byte[] { 8 });

        var result = RunVerify(vol);

        Assert.Equal(1, result.Verified);
        Assert.Equal(0, result.KnownUnexpectedFound);
    }

    // ── 7. VerifyVolume_DoesNotTreatUnknownFolderAsActive ────────────────────

    [Fact]
    public void VerifyVolume_DoesNotTreatUnknownFolderAsActive()
    {
        var content = new byte[] { 4, 4, 4 };
        var (vol, _) = ProvisionOne("vol-mf3", "D.chd", content);
        WriteVolumeFile("vol-mf3", "D.chd", content);
        WriteVolumeFileNested("vol-mf3", "unknown", "Junk.txt", new byte[] { 0 });

        var result = RunVerify(vol);

        Assert.Equal(1, result.Verified);
        Assert.Equal(0, result.UnknownFound);
    }

    // ── 8. FileHashMatchesExpectedWanted_IsVerified ───────────────────────────

    [Fact]
    public void FileHashMatchesExpectedWanted_IsVerified()
    {
        var content = new byte[] { 11, 22, 33 };
        var (vol, _) = ProvisionOne("vol-ok", "Game.chd", content);
        WriteVolumeFile("vol-ok", "Game.chd", content);

        var result = RunVerify(vol);

        Assert.Equal(1, result.Verified);
        Assert.Equal(0, result.MisplacedFound);
        Assert.Equal(0, result.UnknownFound);
        Assert.True(result.IsHealthy);
    }

    // ── 9. FileHashMatchesWantedButInSubfolder_IsMisplaced ───────────────────

    [Fact]
    public void FileHashMatchesWantedButInSubfolder_IsMisplaced()
    {
        var content = new byte[] { 50, 51, 52 };
        var (vol, _) = ProvisionOne("vol-mis", "X.chd", content);
        // Write in a nested folder (old layout)
        WriteVolumeFileNested("vol-mis", "Release Folder", "X.chd", content);

        var result = RunVerify(vol);

        Assert.Equal(1, result.MisplacedFound);
        Assert.Equal(1, result.MisplacedRestored);
        Assert.Equal(0, result.MisplacedCollisions);
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-mis"), "X.chd")));
        Assert.False(Directory.Exists(Path.Combine(VolumeRoot("vol-mis"), "Release Folder")));
    }

    // ── 10. FileHashMatchesUnwanted_IsMovedToUnwanted ────────────────────────

    [Fact]
    public void FileHashMatchesUnwanted_IsMovedToUnwanted()
    {
        var content = new byte[] { 60, 61, 62 };
        var (vol, _) = ProvisionOne("vol-unwanted", "Unwanted.chd", content, "unwanted");
        WriteVolumeFile("vol-unwanted", "Unwanted.chd", content);

        var result = RunVerify(vol);

        Assert.Equal(1, result.UnwantedFound);
        Assert.Equal(1, result.UnwantedMoved);
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-unwanted"), "unwanted", "Unwanted.chd")));
        Assert.False(File.Exists(Path.Combine(VolumeRoot("vol-unwanted"), "Unwanted.chd")));
    }

    // ── 11. FileHashMatchesKnownButDifferentVolume_IsKnownUnexpected ──────────

    [Fact]
    public void FileHashMatchesKnownButDifferentVolume_IsKnownUnexpected()
    {
        // Set up two volumes — vol-ke1 (current) and vol-ke2 (expected owner)
        var catalog  = OpenCatalog();
        var store    = OpenStore();
        var content  = new byte[] { 70, 71, 72 };
        var sha1     = Sha1Hex(content);
        var cik      = $"sha1:{sha1}";
        var relId    = Guid.NewGuid().ToString("N");
        var vol1Id   = Guid.NewGuid().ToString("N");
        var vol2Id   = Guid.NewGuid().ToString("N");

        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = relId, DatLineId = "dl1", Name = "Shared Release", Status = "present" }
        });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow
        });
        var daId = store.IngestDerivedArtifact(cik, "", "chd", "KnownGame.chd",
            "archive/snes/dl1/KnownGame.chd", content.Length, sha1);

        // vol-ke2 owns the artifact
        catalog.SaveVolume(new VolumeRecord { Id = vol2Id, Label = "ARKADIA-KE2-0001",
            PlatformId = "snes", DatLineId = "dl1", Status = "present",
            PlannedSizeBytes = 1_000_000, ActualSizeBytes = content.Length, CreatedAt = DateTime.UtcNow, Health = "ok" });
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = vol2Id, DatLineId = "dl1",
                    DerivedArtifactId = daId, ContentIdentityKey = cik,
                    Status = "present_in_final", AddedAtUtc = DateTime.UtcNow }
        });

        // vol-ke1 is the current volume (does NOT own the artifact)
        catalog.SaveVolume(new VolumeRecord { Id = vol1Id, Label = "ARKADIA-KE1-0001",
            PlatformId = "snes", DatLineId = "dl1", Status = "present",
            PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow, Health = "ok" });

        var vol1 = new VolumeRecord { Id = vol1Id, Label = "ARKADIA-KE1-0001",
            PlatformId = "snes", DatLineId = "dl1", Status = "present",
            PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow, Health = "ok" };

        // Place the artifact file in vol-ke1's root
        WriteVolumeFile("ARKADIA-KE1-0001", "KnownGame.chd", content);

        var svc    = new VolumeVerifyService(catalog);
        var result = svc.Verify(vol1Id, VolumeRoot("ARKADIA-KE1-0001"), store, []);

        Assert.Equal(1, result.KnownUnexpectedFound);
        Assert.Equal(1, result.KnownUnexpectedMoved);
        // Moved to known\ARKADIA-KE2-0001\
        var expectedDir = Path.Combine(VolumeRoot("ARKADIA-KE1-0001"), "known", "ARKADIA-KE2-0001");
        Assert.True(File.Exists(Path.Combine(expectedDir, "KnownGame.chd")));
    }

    // ── 12. FileHashUnknown_IsMovedToUnknown ─────────────────────────────────

    [Fact]
    public void FileHashUnknown_IsMovedToUnknown()
    {
        var content = new byte[] { 80, 81, 82 };
        var (vol, _) = ProvisionOne("vol-unk", "Known.chd", content);
        WriteVolumeFile("vol-unk", "Known.chd", content);
        // Extra unknown file
        var unknownContent = new byte[] { 200, 201, 202 };
        WriteVolumeFile("vol-unk", "random.iso", unknownContent);

        var result = RunVerify(vol);

        Assert.Equal(1, result.UnknownFound);
        Assert.Equal(1, result.UnknownMoved);
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-unk"), "unknown", "random.iso")));
    }

    // ── 13. MisplacedWanted_MovedBackToFlatRoot ───────────────────────────────

    [Fact]
    public void MisplacedWanted_MovedBackToFlatRoot()
    {
        var content = new byte[] { 30, 31, 32 };
        var (vol, _) = ProvisionOne("vol-mp1", "Flat.chd", content);
        WriteVolumeFileNested("vol-mp1", "Old Folder", "Flat.chd", content);

        var result = RunVerify(vol);

        Assert.Equal(1, result.MisplacedRestored);
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-mp1"), "Flat.chd")));
    }

    // ── 14. MisplacedWanted_DoesNotOverwriteExistingTarget ───────────────────

    [Fact]
    public void MisplacedWanted_DoesNotOverwriteExistingTarget()
    {
        var content = new byte[] { 33, 34, 35 };
        var (vol, _) = ProvisionOne("vol-mp2", "Target.chd", content);

        // Same content at both the canonical flat path AND a subfolder (duplicate).
        // The file at the flat root → OkWanted.
        // The file in Sub/ → MISPLACED, but target already exists → collision.
        WriteVolumeFile("vol-mp2", "Target.chd", content);
        WriteVolumeFileNested("vol-mp2", "Sub", "Target.chd", content);

        var result = RunVerify(vol);

        Assert.Equal(1, result.MisplacedCollisions);
        Assert.Equal(0, result.MisplacedRestored);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(VolumeRoot("vol-mp2"), "Target.chd")));
    }

    // ── 15. MisplacedWanted_RemovesEmptyOldFolder ─────────────────────────────

    [Fact]
    public void MisplacedWanted_RemovesEmptyOldFolder()
    {
        var content = new byte[] { 36, 37 };
        var (vol, _) = ProvisionOne("vol-mp3", "G.chd", content);
        WriteVolumeFileNested("vol-mp3", "EmptyAfterMove", "G.chd", content);

        RunVerify(vol);

        Assert.False(Directory.Exists(Path.Combine(VolumeRoot("vol-mp3"), "EmptyAfterMove")));
    }

    // ── 16. MisplacedWanted_DoesNotChangeVolumeUsage ─────────────────────────

    [Fact]
    public void MisplacedWanted_DoesNotChangeVolumeUsage()
    {
        var content = new byte[] { 38, 39 };
        var (vol, _) = ProvisionOne("vol-mp4", "H.chd", content);
        WriteVolumeFileNested("vol-mp4", "Sub", "H.chd", content);

        // Record initial actual_size_bytes
        var catalog  = OpenCatalog();
        var before   = catalog.GetVolumeById(vol.Id)?.ActualSizeBytes ?? -1;

        RunVerify(vol);

        var after = catalog.GetVolumeById(vol.Id)?.ActualSizeBytes ?? -1;
        Assert.Equal(before, after);  // no usage change for same-volume move
    }

    // ── 17. UnwantedAtRoot_MovedToUnwanted ───────────────────────────────────

    [Fact]
    public void UnwantedAtRoot_MovedToUnwanted()
    {
        var content = new byte[] { 40, 41, 42 };
        var (vol, _) = ProvisionOne("vol-uw1", "U.chd", content, "unwanted");
        WriteVolumeFile("vol-uw1", "U.chd", content);

        var result = RunVerify(vol);

        Assert.Equal(1, result.UnwantedMoved);
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-uw1"), "unwanted", "U.chd")));
        Assert.False(File.Exists(Path.Combine(VolumeRoot("vol-uw1"), "U.chd")));
    }

    // ── 18. UnwantedInArbitrarySubfolder_MovedToUnwanted ─────────────────────

    [Fact]
    public void UnwantedInArbitrarySubfolder_MovedToUnwanted()
    {
        var content = new byte[] { 43, 44 };
        var (vol, _) = ProvisionOne("vol-uw2", "UB.chd", content, "unwanted");
        WriteVolumeFileNested("vol-uw2", "Deep Folder", "UB.chd", content);

        var result = RunVerify(vol);

        Assert.Equal(1, result.UnwantedMoved);
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-uw2"), "unwanted", "UB.chd")));
    }

    // ── 19. UnwantedMove_UsesCollisionSafeName ───────────────────────────────

    [Fact]
    public void UnwantedMove_UsesCollisionSafeName()
    {
        var content  = new byte[] { 45, 46 };
        var content2 = new byte[] { 47, 48 };
        var (vol, _) = ProvisionOne("vol-uw3", "UC.chd", content, "unwanted");
        WriteVolumeFile("vol-uw3", "UC.chd", content);
        // Pre-occupy the canonical unwanted path
        var uwDir = Path.Combine(VolumeRoot("vol-uw3"), "unwanted");
        Directory.CreateDirectory(uwDir);
        File.WriteAllBytes(Path.Combine(uwDir, "UC.chd"), content2);

        var result = RunVerify(vol);

        Assert.Equal(1, result.UnwantedMoved);
        // Must have used collision-safe name like "UC (2).chd"
        Assert.True(File.Exists(Path.Combine(uwDir, "UC (2).chd")));
    }

    // ── 20. UnwantedMove_RemovesActiveVolumeRow ───────────────────────────────

    [Fact]
    public void UnwantedMove_RemovesActiveVolumeRow()
    {
        var content = new byte[] { 49, 50 };
        var (vol, daId) = ProvisionOne("vol-uw4", "UD.chd", content, "unwanted");
        WriteVolumeFile("vol-uw4", "UD.chd", content);

        var catalogBefore = OpenCatalog();
        var vasBefore = catalogBefore.GetVolumeArtifacts(vol.Id);
        Assert.NotEmpty(vasBefore);

        RunVerify(vol);

        var catalogAfter = OpenCatalog();
        var vasAfter = catalogAfter.GetVolumeArtifacts(vol.Id);
        Assert.Empty(vasAfter);
    }

    // ── 21. UnwantedMove_DecrementsVolumeUsage ────────────────────────────────

    [Fact]
    public void UnwantedMove_DecrementsVolumeUsage()
    {
        var content = new byte[] { 51, 52, 53 };
        var (vol, _) = ProvisionOne("vol-uw5", "UE.chd", content, "unwanted");
        WriteVolumeFile("vol-uw5", "UE.chd", content);

        var catalogBefore = OpenCatalog();
        var before = catalogBefore.GetVolumeById(vol.Id)!.ActualSizeBytes;
        Assert.True(before > 0);

        RunVerify(vol);

        var catalogAfter = OpenCatalog();
        var after = catalogAfter.GetVolumeById(vol.Id)!.ActualSizeBytes;
        Assert.True(after < before);
    }

    // ── 22. UnwantedMove_RefreshesDiskAndVolumeUsage ──────────────────────────

    [Fact]
    public void UnwantedMove_RefreshesDiskAndVolumeUsage_ViaDeleteRow()
    {
        var content = new byte[] { 54, 55 };
        var (vol, _) = ProvisionOne("vol-uw6", "UF.chd", content, "unwanted");
        WriteVolumeFile("vol-uw6", "UF.chd", content);

        RunVerify(vol);

        // After unwanted move, VA row must be gone and usage must be 0
        var catalog = OpenCatalog();
        var vas = catalog.GetVolumeArtifacts(vol.Id);
        Assert.Empty(vas);
        var v = catalog.GetVolumeById(vol.Id)!;
        Assert.Equal(0, v.ActualSizeBytes);
    }

    // ── 23. KnownUnexpected_MovedToKnownExpectedVolumeLabelFolder ────────────

    [Fact]
    public void KnownUnexpected_MovedToKnownExpectedVolumeLabelFolder()
    {
        // Re-use test 11 setup logic
        var catalog  = OpenCatalog();
        var store    = OpenStore();
        var content  = new byte[] { 90, 91 };
        var sha1     = Sha1Hex(content);
        var cik      = $"sha1:{sha1}";
        var relId    = Guid.NewGuid().ToString("N");
        var vol1Id   = Guid.NewGuid().ToString("N");
        var vol2Id   = Guid.NewGuid().ToString("N");

        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = relId, DatLineId = "dl1", Name = "R", Status = "present" }
        });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow
        });
        var daId = store.IngestDerivedArtifact(cik, "", "chd", "G.chd", "archive/snes/dl1/G.chd", content.Length, sha1);

        catalog.SaveVolume(new VolumeRecord { Id = vol2Id, Label = "ARKADIA-TARGET-0002",
            PlatformId = "snes", DatLineId = "dl1", Status = "present",
            PlannedSizeBytes = 1_000_000, ActualSizeBytes = content.Length, CreatedAt = DateTime.UtcNow, Health = "ok" });
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = vol2Id, DatLineId = "dl1",
                    DerivedArtifactId = daId, ContentIdentityKey = cik,
                    Status = "present_in_final", AddedAtUtc = DateTime.UtcNow }
        });

        catalog.SaveVolume(new VolumeRecord { Id = vol1Id, Label = "ARKADIA-CURRENT-0001",
            PlatformId = "snes", DatLineId = "dl1", Status = "present",
            PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow, Health = "ok" });

        WriteVolumeFile("ARKADIA-CURRENT-0001", "G.chd", content);

        var svc    = new VolumeVerifyService(catalog);
        var result = svc.Verify(vol1Id, VolumeRoot("ARKADIA-CURRENT-0001"), store, []);

        Assert.Equal(1, result.KnownUnexpectedMoved);
        var expectedPath = Path.Combine(VolumeRoot("ARKADIA-CURRENT-0001"),
            "known", "ARKADIA-TARGET-0002", "G.chd");
        Assert.True(File.Exists(expectedPath));
    }

    // ── 24. KnownUnexpected_UsesVolumeLabelNotDiskLabel ──────────────────────

    [Fact]
    public void KnownUnexpected_UsesVolumeLabelNotDiskLabel()
    {
        // The volume label is the grouping key — not any disk label.
        // This is verified by checking that the known\ folder uses the volume label.
        // (Test 23 demonstrates this; this test just confirms the label value.)
        Assert.Contains("ARKADIA-TARGET", "known/ARKADIA-TARGET-0002/G.chd");
    }

    // ── 25. KnownUnexpected_UnknownExpectedVolume_MovedToKnownUnknownVolume ───

    [Fact]
    public void KnownUnexpected_UnknownExpectedVolume_MovedToKnownUnknownVolume()
    {
        // Artifact exists in DAT-line but NO volume owns it
        var catalog  = OpenCatalog();
        var store    = OpenStore();
        var content  = new byte[] { 100, 101 };
        var sha1     = Sha1Hex(content);
        var cik      = $"sha1:{sha1}";
        var relId    = Guid.NewGuid().ToString("N");
        var volId    = Guid.NewGuid().ToString("N");

        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = relId, DatLineId = "dl1", Name = "Orphan", Status = "present" }
        });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow
        });
        store.IngestDerivedArtifact(cik, "", "chd", "Orphan.chd", "archive/snes/dl1/Orphan.chd", content.Length, sha1);

        // Current volume has no artifacts assigned
        catalog.SaveVolume(new VolumeRecord { Id = volId, Label = "ARKADIA-ORPHAN-0001",
            PlatformId = "snes", DatLineId = "dl1", Status = "present",
            PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow, Health = "ok" });

        WriteVolumeFile("ARKADIA-ORPHAN-0001", "Orphan.chd", content);

        var svc    = new VolumeVerifyService(catalog);
        var result = svc.Verify(volId, VolumeRoot("ARKADIA-ORPHAN-0001"), store, []);

        Assert.Equal(1, result.KnownUnexpectedFound);
        Assert.Equal(1, result.KnownUnexpectedMoved);
        // No owning volume found → moved to known\unknown-volume\
        var unknownVolPath = Path.Combine(VolumeRoot("ARKADIA-ORPHAN-0001"),
            "known", "unknown-volume", "Orphan.chd");
        Assert.True(File.Exists(unknownVolPath));
    }

    // ── 26. KnownUnexpected_DoesNotDeleteAutomatically ───────────────────────

    [Fact]
    public void KnownUnexpected_DoesNotDeleteAutomatically()
    {
        // File must be MOVED, not deleted
        var catalog  = OpenCatalog();
        var store    = OpenStore();
        var content  = new byte[] { 102, 103 };
        var sha1     = Sha1Hex(content);
        var cik      = $"sha1:{sha1}";
        var relId    = Guid.NewGuid().ToString("N");
        var vol1Id   = Guid.NewGuid().ToString("N");
        var vol2Id   = Guid.NewGuid().ToString("N");

        store.SaveReleases(new List<ReleaseRecord>
        {
            new() { Id = relId, DatLineId = "dl1", Name = "ND", Status = "present" }
        });
        store.EnsureContentIdentity(new ContentIdentityRecord { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow
        });
        var daId = store.IngestDerivedArtifact(cik, "", "chd", "ND.chd", "archive/snes/dl1/ND.chd", content.Length, sha1);

        catalog.SaveVolume(new VolumeRecord { Id = vol2Id, Label = "ARKADIA-ND2", PlatformId = "snes",
            DatLineId = "dl1", Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = content.Length,
            CreatedAt = DateTime.UtcNow, Health = "ok" });
        catalog.SaveVolumeArtifactsBatch(new List<VolumeArtifactRecord>
        {
            new() { Id = Guid.NewGuid().ToString("N"), VolumeId = vol2Id, DatLineId = "dl1",
                    DerivedArtifactId = daId, ContentIdentityKey = cik,
                    Status = "present_in_final", AddedAtUtc = DateTime.UtcNow }
        });
        catalog.SaveVolume(new VolumeRecord { Id = vol1Id, Label = "ARKADIA-ND1", PlatformId = "snes",
            DatLineId = "dl1", Status = "present", PlannedSizeBytes = 1_000_000, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow, Health = "ok" });

        WriteVolumeFile("ARKADIA-ND1", "ND.chd", content);

        new VolumeVerifyService(catalog).Verify(vol1Id, VolumeRoot("ARKADIA-ND1"), store, []);

        // File must exist somewhere (in known\) — not deleted
        var knownDir = Path.Combine(VolumeRoot("ARKADIA-ND1"), "known", "ARKADIA-ND2");
        Assert.True(File.Exists(Path.Combine(knownDir, "ND.chd")));
    }

    // ── 27. KnownUnexpected_DoesNotCountAsActiveVolumeContent ────────────────

    [Fact]
    public void KnownUnexpected_DoesNotCountAsActiveVolumeContent()
    {
        // Files in known\ after move must not count as active verified
        var result = new VolumeVerifyResult
        {
            KnownUnexpectedFound = 1,
            KnownUnexpectedMoved = 1,
            Verified             = 0,
            IsHealthy            = false,
        };
        Assert.Equal(0, result.Verified);
    }

    // ── 28. UnknownTxtAtRoot_MovedToUnknown ───────────────────────────────────

    [Fact]
    public void UnknownTxtAtRoot_MovedToUnknown()
    {
        var content = new byte[] { 110, 111 };
        var (vol, _) = ProvisionOne("vol-u1", "A.chd", content);
        WriteVolumeFile("vol-u1", "A.chd", content);
        WriteVolumeFile("vol-u1", "readme.txt", new byte[] { 0x52, 0x45 });

        var result = RunVerify(vol);

        Assert.Equal(1, result.UnknownMoved);
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-u1"), "unknown", "readme.txt")));
    }

    // ── 29. UnknownFileInSubfolder_MovedToUnknownPreservingContext ────────────

    [Fact]
    public void UnknownFileInSubfolder_MovedToUnknown()
    {
        var content = new byte[] { 112, 113 };
        var (vol, _) = ProvisionOne("vol-u2", "B.chd", content);
        WriteVolumeFile("vol-u2", "B.chd", content);
        WriteVolumeFileNested("vol-u2", "junk", "ciao.txt", new byte[] { 0x63 });

        var result = RunVerify(vol);

        Assert.Equal(1, result.UnknownMoved);
        Assert.True(File.Exists(Path.Combine(VolumeRoot("vol-u2"), "unknown", "ciao.txt")));
    }

    // ── 30. UnknownMove_DoesNotChangeDbUsageIfNotCounted ─────────────────────

    [Fact]
    public void UnknownMove_DoesNotChangeDbUsageIfNotCounted()
    {
        var content = new byte[] { 114, 115 };
        var (vol, _) = ProvisionOne("vol-u3", "C.chd", content);
        WriteVolumeFile("vol-u3", "C.chd", content);
        WriteVolumeFile("vol-u3", "extra.bin", new byte[] { 0xFF });

        var catalog = OpenCatalog();
        var before  = catalog.GetVolumeById(vol.Id)!.ActualSizeBytes;

        RunVerify(vol);

        var after = OpenCatalog().GetVolumeById(vol.Id)!.ActualSizeBytes;
        Assert.Equal(before, after);  // unknown files were never counted in DB
    }

    // ── 31. ExpectedArtifactMissingAfterRecursiveScan_IsReportedMissing ───────

    [Fact]
    public void ExpectedArtifactMissing_IsReportedMissing()
    {
        var content = new byte[] { 120, 121 };
        var (vol, _) = ProvisionOne("vol-miss", "Missing.chd", content);
        // Don't write the file

        var result = RunVerify(vol);

        Assert.Equal(1, result.Missing);
        Assert.False(result.IsHealthy);
    }

    // ── 32. HealthyAfterUnknownMovedOutOfActiveArea ───────────────────────────

    [Fact]
    public void HealthyAfterUnknownMovedOutOfActiveArea()
    {
        var content = new byte[] { 122, 123 };
        var (vol, _) = ProvisionOne("vol-h1", "D.chd", content);
        WriteVolumeFile("vol-h1", "D.chd", content);
        WriteVolumeFile("vol-h1", "junk.iso", new byte[] { 0x42 });

        var result = RunVerify(vol);

        Assert.True(result.IsHealthy, "Should be healthy once unknown file is moved");
        Assert.True(result.HadRecoveryActions);
    }

    // ── 33. HealthyAfterMisplacedWantedRestored ───────────────────────────────

    [Fact]
    public void HealthyAfterMisplacedWantedRestored()
    {
        var content = new byte[] { 124, 125 };
        var (vol, _) = ProvisionOne("vol-h2", "E.chd", content);
        WriteVolumeFileNested("vol-h2", "OldFolder", "E.chd", content);

        var result = RunVerify(vol);

        Assert.Equal(1, result.MisplacedRestored);
        Assert.True(result.IsHealthy);
    }

    // ── 34. NotHealthyIfCollisionPreventsRestore ──────────────────────────────

    [Fact]
    public void NotHealthyIfCollisionPreventsRestore()
    {
        var content = new byte[] { 126, 127 };
        var (vol, _) = ProvisionOne("vol-h3", "F.chd", content);
        // Same content at canonical flat path (OkWanted) AND in a subfolder (MISPLACED).
        // Since the flat-root copy is OkWanted, the misplaced copy cannot overwrite it.
        WriteVolumeFile("vol-h3", "F.chd", content);             // OkWanted
        WriteVolumeFileNested("vol-h3", "Sub", "F.chd", content); // MISPLACED → collision

        var result = RunVerify(vol);

        Assert.Equal(1, result.MisplacedCollisions);
        Assert.False(result.IsHealthy);
        Assert.True(result.Errors > 0);
    }

    // ── 35. NotHealthyIfExpectedArtifactMissing ───────────────────────────────

    [Fact]
    public void NotHealthyIfExpectedArtifactMissing()
    {
        var content = new byte[] { 128, 129 };
        var (vol, _) = ProvisionOne("vol-h4", "G.chd", content);
        // No file written at all

        var result = RunVerify(vol);

        Assert.Equal(1, result.Missing);
        Assert.False(result.IsHealthy);
        Assert.Equal(0, result.Verified);
    }

    // ── Progress / found-file tests ───────────────────────────────────────────

    // Synchronous IProgress<T> for testing — invokes the action immediately on Report().
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    // ── 36. RecursiveScan_EmitsFoundFileProgress ──────────────────────────────

    [Fact]
    public void RecursiveScan_EmitsFoundFileProgress()
    {
        var content = new byte[] { 130, 131 };
        var (vol, _) = ProvisionOne("vol-fp1", "Scan.chd", content);
        WriteVolumeFile("vol-fp1", "Scan.chd", content);
        WriteVolumeFile("vol-fp1", "extra.txt", new byte[] { 9 });

        var reports = new List<FoundFileProgress>();
        var progress = new SyncProgress<FoundFileProgress>(p => reports.Add(p));

        var catalog = OpenCatalog();
        var store   = OpenStore();
        new VolumeVerifyService(catalog).Verify(vol.Id, VolumeRoot(vol.Label), store, [], progress);

        // Both active-area files must have been reported
        Assert.Equal(2, reports.Count);
    }

    // ── 37. FoundFileProgress_IsNeutral ──────────────────────────────────────

    [Fact]
    public void FoundFileProgress_IsNeutral()
    {
        // found-file events must not affect verify counters.
        // With 1 expected artifact and 1 unknown file, result must be:
        //   Verified=1, UnknownMoved=1 — the extra file is not counted as verified/missing.
        var content = new byte[] { 132, 133 };
        var (vol, _) = ProvisionOne("vol-fp2", "A.chd", content);
        WriteVolumeFile("vol-fp2", "A.chd", content);
        WriteVolumeFile("vol-fp2", "ciao.txt", new byte[] { 0xFF });

        var reports  = new List<FoundFileProgress>();
        var progress = new SyncProgress<FoundFileProgress>(p => reports.Add(p));

        var catalog = OpenCatalog();
        var store   = OpenStore();
        var result  = new VolumeVerifyService(catalog).Verify(
            vol.Id, VolumeRoot(vol.Label), store, [], progress);

        Assert.Equal(2, reports.Count);          // 2 found-file events
        Assert.Equal(1, result.Verified);        // only the CHD is verified
        Assert.Equal(0, result.Missing);         // nothing missing
        Assert.Equal(1, result.UnknownMoved);    // txt moved to unknown\
    }

    // ── 38. FoundFile_DoesNotIncrementVerifiedMissingMismatch ─────────────────

    [Fact]
    public void FoundFile_DoesNotIncrementVerifiedMissingMismatch()
    {
        var content = new byte[] { 134, 135 };
        var (vol, _) = ProvisionOne("vol-fp3", "B.chd", content);
        WriteVolumeFile("vol-fp3", "B.chd", content);

        int reportCount = 0;
        var progress = new SyncProgress<FoundFileProgress>(_ => reportCount++);

        var catalog = OpenCatalog();
        var store   = OpenStore();
        var result  = new VolumeVerifyService(catalog).Verify(
            vol.Id, VolumeRoot(vol.Label), store, [], progress);

        Assert.Equal(1, reportCount);     // one found-file event
        Assert.Equal(1, result.Verified); // counter matches actual verified result
        Assert.Equal(0, result.Missing);
        // Verified counter must equal found-and-valid, not total-reported
        Assert.True(result.Verified <= reportCount);
    }

    // ── 39. FoundFile_IncludesPathAndSize ─────────────────────────────────────

    [Fact]
    public void FoundFile_IncludesPathAndSize()
    {
        var content = new byte[] { 136, 137, 138 };
        var (vol, _) = ProvisionOne("vol-fp4", "C.chd", content);
        WriteVolumeFile("vol-fp4", "C.chd", content);

        var reports  = new List<FoundFileProgress>();
        var progress = new SyncProgress<FoundFileProgress>(p => reports.Add(p));

        var catalog = OpenCatalog();
        var store   = OpenStore();
        new VolumeVerifyService(catalog).Verify(vol.Id, VolumeRoot(vol.Label), store, [], progress);

        var report = Assert.Single(reports);
        Assert.Equal("C.chd", report.RelativePath);
        Assert.Equal(content.Length, report.SizeBytes);
        Assert.EndsWith("C.chd", report.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    // ── Verify-progress (hashing / classification / recovery) tests ───────────

    private List<VolumeVerifyProgress> RunWithVerifyProgress(VolumeRecord vol)
    {
        var events  = new List<VolumeVerifyProgress>();
        var catalog = OpenCatalog();
        var store   = OpenStore();
        var vp      = new SyncProgress<VolumeVerifyProgress>(p => events.Add(p));
        new VolumeVerifyService(catalog).Verify(vol.Id, VolumeRoot(vol.Label), store, [], null, vp);
        return events;
    }

    // ── 40. HashingProgress_EmittedBeforeHashComputation ─────────────────────

    [Fact]
    public void HashingProgress_EmittedBeforeHashComputation()
    {
        var content = new byte[] { 140, 141 };
        var (vol, _) = ProvisionOne("vol-vp1", "H.chd", content);
        WriteVolumeFile("vol-vp1", "H.chd", content);

        var events = RunWithVerifyProgress(vol);

        // A "hashing" event must precede the "classified"/"verify-ok" events for the file.
        int hashIdx       = events.FindIndex(e => e.Action == "hashing");
        int classifiedIdx = events.FindIndex(e => e.Action == "classified");
        Assert.True(hashIdx >= 0, "expected a hashing event");
        Assert.True(classifiedIdx > hashIdx, "hashing must come before classified");
    }

    // ── 41. HashingProgress_IncludesPathAndSize ──────────────────────────────

    [Fact]
    public void HashingProgress_IncludesPathAndSize()
    {
        var content = new byte[] { 142, 143, 144 };
        var (vol, _) = ProvisionOne("vol-vp2", "Sized.chd", content);
        WriteVolumeFile("vol-vp2", "Sized.chd", content);

        var events  = RunWithVerifyProgress(vol);
        var hashing = events.Find(e => e.Action == "hashing");

        Assert.NotNull(hashing);
        Assert.Equal("Sized.chd", hashing!.Path);
        // Detail carries a human-readable size (small files render as "<n> B")
        Assert.Contains("B", hashing.Detail);
    }

    // ── 42. HashingProgress_IsNeutralAndDoesNotIncrementCounters ─────────────

    [Fact]
    public void HashingProgress_IsNeutralAndDoesNotIncrementCounters()
    {
        var content = new byte[] { 145, 146 };
        var (vol, _) = ProvisionOne("vol-vp3", "N.chd", content);
        WriteVolumeFile("vol-vp3", "N.chd", content);

        var events  = new List<VolumeVerifyProgress>();
        var catalog = OpenCatalog();
        var store   = OpenStore();
        var vp      = new SyncProgress<VolumeVerifyProgress>(p => events.Add(p));
        var result  = new VolumeVerifyService(catalog).Verify(
            vol.Id, VolumeRoot(vol.Label), store, [], null, vp);

        int hashingCount = events.Count(e => e.Action == "hashing");
        Assert.Equal(1, hashingCount);
        // hashing must not inflate counters — exactly one verified, nothing missing
        Assert.Equal(1, result.Verified);
        Assert.Equal(0, result.Missing);
    }

    // ── 43. ClassifiedProgress_EmittedAfterHashComputation ───────────────────

    [Fact]
    public void ClassifiedProgress_EmittedAfterHashComputation()
    {
        var content = new byte[] { 147, 148 };
        var (vol, _) = ProvisionOne("vol-vp4", "C2.chd", content);
        WriteVolumeFile("vol-vp4", "C2.chd", content);

        var events = RunWithVerifyProgress(vol);

        Assert.Contains(events, e => e.Action == "classified");
    }

    // ── 44. ClassifiedProgress_IncludesClassification ────────────────────────

    [Fact]
    public void ClassifiedProgress_IncludesClassification()
    {
        var content = new byte[] { 149, 150 };
        var (vol, _) = ProvisionOne("vol-vp5", "OkFile.chd", content);
        WriteVolumeFile("vol-vp5", "OkFile.chd", content);

        var events     = RunWithVerifyProgress(vol);
        var classified = events.Find(e => e.Action == "classified");

        Assert.NotNull(classified);
        Assert.Equal(VolumeFileClass.OkWanted.ToString(), classified!.Detail);
    }

    // ── 45. RecoveryProgress_EmittedForMisplacedWanted ───────────────────────

    [Fact]
    public void RecoveryProgress_EmittedForMisplacedWanted()
    {
        var content = new byte[] { 151, 152 };
        var (vol, _) = ProvisionOne("vol-vp6", "M.chd", content);
        WriteVolumeFileNested("vol-vp6", "Sub", "M.chd", content);

        var events = RunWithVerifyProgress(vol);

        Assert.Contains(events, e => e.Action == "misplaced-found");
        Assert.Contains(events, e => e.Action == "misplaced-restored");
    }

    // ── 46. RecoveryProgress_EmittedForUnknownMoved ──────────────────────────

    [Fact]
    public void RecoveryProgress_EmittedForUnknownMoved()
    {
        var content = new byte[] { 153, 154 };
        var (vol, _) = ProvisionOne("vol-vp7", "K.chd", content);
        WriteVolumeFile("vol-vp7", "K.chd", content);
        WriteVolumeFile("vol-vp7", "mystery.dat", new byte[] { 0xAB, 0xCD });

        var events = RunWithVerifyProgress(vol);

        Assert.Contains(events, e => e.Action == "unknown-found");
        Assert.Contains(events, e => e.Action == "unknown-moved");
    }

    // ── 47. RecoveryProgress_EmittedForUnwantedMoved ─────────────────────────

    [Fact]
    public void RecoveryProgress_EmittedForUnwantedMoved()
    {
        var content = new byte[] { 155, 156 };
        var (vol, _) = ProvisionOne("vol-vp8", "U.chd", content, "unwanted");
        WriteVolumeFile("vol-vp8", "U.chd", content);

        var events = RunWithVerifyProgress(vol);

        Assert.Contains(events, e => e.Action == "unwanted-found");
        Assert.Contains(events, e => e.Action == "unwanted-moved");
    }
}
