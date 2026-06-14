using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Arkadia.LocalArchive;
using Xunit;

namespace Arkadia.Tests.LocalArchive;

/// <summary>
/// Tests for LocalArchiveVerifyService — filesystem-first classification and repair.
///
/// Core invariant: Verify() only emits entries for physical files found on disk.
/// DB artifacts absent from the archive directory appear only in AbsentFromArchiveCount,
/// NOT in plan.Entries.
/// </summary>
public sealed class LocalArchiveVerifyServiceTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _datDbPath;
    private readonly string _appRoot;
    private const    string PlatformId = "ps2";
    private const    string DatLineId  = "ps2-redump-dvd";

    public LocalArchiveVerifyServiceTests()
    {
        _tmp       = Path.Combine(Path.GetTempPath(), "ArkLAV_" + Guid.NewGuid().ToString("N")[..8]);
        _appRoot   = Path.Combine(_tmp, "approot");
        _datDbPath = Path.Combine(_tmp, "dat.db");
        Directory.CreateDirectory(_tmp);
        Directory.CreateDirectory(_appRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private DatLineStore           OpenStore()   => new(_datDbPath);
    private LocalArchiveVerifyService MakeService() => new(_appRoot);

    private string ArchiveDir => Path.Combine(_appRoot, "archive", PlatformId, DatLineId);
    private string SkipDir    => Path.Combine(_appRoot, "incoming-skip", PlatformId);

    private (string RelId, string DaId, string Sha1) ProvisionArtifact(
        string relStatus, string fileName, byte[]? content = null)
    {
        content ??= System.Text.Encoding.UTF8.GetBytes($"content-{fileName}");
        var sha1  = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(content)).ToLowerInvariant();
        var cik   = $"sha1:{sha1}";
        var relId = Guid.NewGuid().ToString("N");
        var store = OpenStore();

        store.UpsertRelease(new ReleaseRecord
        {
            Id = relId, DatLineId = DatLineId, Name = "Release " + fileName, Status = relStatus
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
        var daId = store.IngestDerivedArtifact(cik, "", "chd", fileName,
            $"archive/{PlatformId}/{DatLineId}/{fileName}", content.Length, sha1);

        return (relId, daId, sha1);
    }

    private void WriteArchiveFile(string fileName, byte[] content)
    {
        Directory.CreateDirectory(ArchiveDir);
        File.WriteAllBytes(Path.Combine(ArchiveDir, fileName), content);
    }

    // ── 1. Scan is filesystem-first — only physical files appear in Entries ──

    [Fact]
    public void VerifyArchive_ScansPhysicalArchiveFilesOnly()
    {
        // Two DB artifacts; only one has a physical file.
        ProvisionArtifact("present", "Physical.chd",
            System.Text.Encoding.UTF8.GetBytes("physical-content"));
        ProvisionArtifact("present", "DatabaseOnly.chd",
            System.Text.Encoding.UTF8.GetBytes("db-only-content"));

        WriteArchiveFile("Physical.chd",
            System.Text.Encoding.UTF8.GetBytes("physical-content"));
        // DatabaseOnly.chd is NOT written to disk.

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());

        // Only the physical file appears in Entries.
        Assert.Equal(1, plan.FilesScanned);
        Assert.Single(plan.Entries);
        Assert.Equal("Physical.chd", plan.Entries[0].FileName);
    }

    // ── 2. Absent DB artifacts go to AbsentFromArchiveCount, not Entries ────

    [Fact]
    public void VerifyArchive_DoesNotReportAbsentDbArtifactsAsMainMissingEntries()
    {
        // 3 DB artifacts, none have physical files.
        ProvisionArtifact("present", "Missing1.chd");
        ProvisionArtifact("present", "Missing2.chd");
        ProvisionArtifact("present", "Missing3.chd");

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());

        // Primary scan entries: zero (no physical files).
        Assert.Equal(0, plan.FilesScanned);
        Assert.Empty(plan.Entries);

        // Diagnostic counter reflects the absent artifacts.
        Assert.Equal(3, plan.AbsentFromArchiveCount);

        // IsClean must not be affected by absent artifacts.
        Assert.True(plan.IsClean);
    }

    // ── 3. Wanted physical file classified as WantedArchiveOk ───────────────

    [Fact]
    public void VerifyArchive_ClassifiesWantedPhysicalFileAsWantedOk()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("ok-content");
        var (_, daId, _) = ProvisionArtifact("present", "Good.chd", content);
        WriteArchiveFile("Good.chd", content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());

        Assert.Equal(1, plan.WantedOk);
        var entry = plan.Entries.Single(e => e.DerivedArtifactId == daId);
        Assert.Equal(LocalArchiveClass.WantedArchiveOk, entry.Classification);
        Assert.False(entry.IsRepairable);
    }

    // ── 4. Unwanted physical file classified as UnwantedArchiveArtifact ─────

    [Fact]
    public void VerifyArchive_ClassifiesUnwantedPhysicalFileAsUnwantedArchiveArtifact()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("unwanted-content");
        var (_, daId, _) = ProvisionArtifact("unwanted", "Bad.chd", content);
        WriteArchiveFile("Bad.chd", content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());

        Assert.Equal(1, plan.UnwantedArtifacts);
        Assert.Equal(1, plan.RepairableCount);
        var entry = plan.Entries.Single(e => e.DerivedArtifactId == daId);
        Assert.Equal(LocalArchiveClass.UnwantedArchiveArtifact, entry.Classification);
        Assert.True(entry.IsRepairable);
    }

    // ── 5. Physical file with no DB match classified as UnknownArchiveFile ──

    [Fact]
    public void VerifyArchive_ClassifiesUnknownPhysicalFileAsUnknownArchiveFile()
    {
        WriteArchiveFile("Unknown.chd",
            System.Text.Encoding.UTF8.GetBytes("totally-unknown-content"));

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());

        Assert.Equal(1, plan.UnknownFiles);
        var entry = plan.Entries.Single(e => e.FileName == "Unknown.chd");
        Assert.Equal(LocalArchiveClass.UnknownArchiveFile, entry.Classification);
        Assert.True(entry.IsRepairable);
    }

    // ── 6. HashMismatch: filename matches DB but hash does not ───────────────

    [Fact]
    public void VerifyArchive_ClassifiesHashMismatchCorrectly()
    {
        var original  = System.Text.Encoding.UTF8.GetBytes("original-content");
        var corrupted = System.Text.Encoding.UTF8.GetBytes("corrupted-content");
        var (_, daId, _) = ProvisionArtifact("present", "Corrupt.chd", original);
        WriteArchiveFile("Corrupt.chd", corrupted);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());

        Assert.Equal(1, plan.HashMismatches);
        var entry = plan.Entries.Single(e => e.DerivedArtifactId == daId);
        Assert.Equal(LocalArchiveClass.ArchiveHashMismatch, entry.Classification);
        Assert.True(entry.IsRepairable);
    }

    // ── 7. Repair moves unwanted artifact to incoming-skip\<platform>\ ───────

    [Fact]
    public void VerifyArchive_RepairMovesUnwantedToIncomingSkipPlatform()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("unwanted-to-repair");
        var (_, daId, _) = ProvisionArtifact("unwanted", "ToRepair.chd", content);
        var archivePath   = Path.Combine(ArchiveDir, "ToRepair.chd");
        WriteArchiveFile("ToRepair.chd", content);

        var plan   = MakeService().Verify(PlatformId, DatLineId, OpenStore());
        var result = MakeService().Repair(plan, OpenStore());

        Assert.True(result.Success);
        Assert.Equal(1, result.MovedToSkip);
        Assert.False(File.Exists(archivePath), "Archive file should have been moved");
        Assert.True(File.Exists(Path.Combine(SkipDir, "ToRepair.chd")),
            "File should be in incoming-skip/<platform>/");
    }

    // ── 8. Repair moves unknown file to incoming-skip\<platform>\ ────────────

    [Fact]
    public void VerifyArchive_RepairMovesUnknownToIncomingSkipPlatform()
    {
        var archivePath = Path.Combine(ArchiveDir, "Unknown.chd");
        WriteArchiveFile("Unknown.chd",
            System.Text.Encoding.UTF8.GetBytes("unknown-content"));

        var plan   = MakeService().Verify(PlatformId, DatLineId, OpenStore());
        var result = MakeService().Repair(plan, OpenStore());

        Assert.True(result.Success);
        Assert.Equal(1, result.MovedToSkip);
        Assert.False(File.Exists(archivePath), "Unknown archive file should have been moved");
        Assert.True(File.Exists(Path.Combine(SkipDir, "Unknown.chd")),
            "Unknown file should be in incoming-skip/<platform>/");
    }

    // ── 9. Repair does not touch wanted archive artifacts ────────────────────

    [Fact]
    public void VerifyArchive_DoesNotTouchWantedOk()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("wanted-safe");
        var (_, daId, _) = ProvisionArtifact("present", "WantedSafe.chd", content);
        WriteArchiveFile("WantedSafe.chd", content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());
        MakeService().Repair(plan, OpenStore());

        Assert.True(File.Exists(Path.Combine(ArchiveDir, "WantedSafe.chd")));
        var infos = OpenStore().GetAllArchiveArtifactInfos();
        Assert.Contains(infos, a => a.DerivedArtifactId == daId);
    }

    // ── 10. Repair uses collision-safe filenames ──────────────────────────────

    [Fact]
    public void VerifyArchive_UsesCollisionSafeIncomingSkipNames()
    {
        Directory.CreateDirectory(SkipDir);
        File.WriteAllBytes(Path.Combine(SkipDir, "Collision.chd"),
            System.Text.Encoding.UTF8.GetBytes("pre-existing"));

        var content = System.Text.Encoding.UTF8.GetBytes("collision-content");
        ProvisionArtifact("unwanted", "Collision.chd", content);
        WriteArchiveFile("Collision.chd", content);

        var plan   = MakeService().Verify(PlatformId, DatLineId, OpenStore());
        var result = MakeService().Repair(plan, OpenStore());

        Assert.True(result.Success);
        Assert.Equal(1, result.MovedToSkip);
        Assert.True(File.Exists(Path.Combine(SkipDir, "Collision.chd")),
            "Pre-existing file must be untouched");
        Assert.True(File.Exists(Path.Combine(SkipDir, "Collision (2).chd")),
            "Collision-safe copy must exist");
    }

    // ── 11. Progress callbacks fire for each physical file ───────────────────

    [Fact]
    public void VerifyArchive_ProgressReportsFoundHashingClassified()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("progress-content");
        ProvisionArtifact("present", "Progress.chd", content);
        WriteArchiveFile("Progress.chd", content);

        var actions = new List<string>();
        var progress = new DelegateProgress<LocalArchiveVerifyProgress>(
            p => actions.Add(p.Action));

        MakeService().Verify(PlatformId, DatLineId, OpenStore(), progress);

        Assert.Contains("archive-found-file", actions);
        Assert.Contains("archive-hashing",    actions);
        Assert.Contains("archive-wanted-ok",  actions);
    }

    // ── 12. Main stats do not include whole-library missing count ────────────

    [Fact]
    public void VerifyArchive_MainStatsDoNotIncludeWholeLibraryMissingCount()
    {
        // Provision 100 DB artifacts, none with physical files.
        for (int i = 0; i < 100; i++)
            ProvisionArtifact("present", $"Game{i:D3}.chd",
                System.Text.Encoding.UTF8.GetBytes($"content-{i}"));

        // Only one physical file — an unwanted one.
        var bad = System.Text.Encoding.UTF8.GetBytes("bad-content");
        ProvisionArtifact("unwanted", "Unwanted.chd", bad);
        WriteArchiveFile("Unwanted.chd", bad);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());

        // Main scan: only the one physical file.
        Assert.Equal(1, plan.FilesScanned);
        Assert.Equal(0, plan.WantedOk);
        Assert.Equal(1, plan.UnwantedArtifacts);

        // The 100 absent DB artifacts go to the diagnostic counter, not Entries.
        Assert.Equal(100, plan.AbsentFromArchiveCount);
        Assert.Single(plan.Entries); // not 101

        // UI must not say the archive is missing 100 files.
        Assert.False(plan.IsClean); // still dirty (unwanted artifact)
    }

    // ── 13. Repair removes DA rows for unwanted artifacts ────────────────────

    [Fact]
    public void VerifyArchive_RepairRemovesUnwantedDerivedArtifactRows()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("removable-content");
        var (_, daId, _) = ProvisionArtifact("unwanted", "Removable.chd", content);
        WriteArchiveFile("Removable.chd", content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());
        MakeService().Repair(plan, OpenStore());

        var archiveInfos = OpenStore().GetAllArchiveArtifactInfos();
        Assert.DoesNotContain(archiveInfos, a => a.DerivedArtifactId == daId);
    }

    // ── 14. Repair preserves release unwanted status ─────────────────────────

    [Fact]
    public void VerifyArchive_RepairPreservesReleaseUnwantedStatus()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("still-unwanted");
        var (relId, _, _) = ProvisionArtifact("unwanted", "Unwanted.chd", content);
        WriteArchiveFile("Unwanted.chd", content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());
        MakeService().Repair(plan, OpenStore());

        var rel = OpenStore().LoadReleasesByDatLine(DatLineId).Find(r => r.Id == relId);
        Assert.NotNull(rel);
        Assert.Equal("unwanted", rel!.Status);
    }

    // ── 15. IsClean when no physical files have issues ───────────────────────

    [Fact]
    public void VerifyArchive_IsClean_WhenAllPhysicalFilesAreWantedOk()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("clean-content");
        ProvisionArtifact("present", "Clean.chd", content);
        WriteArchiveFile("Clean.chd", content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());
        Assert.True(plan.IsClean);
    }

    // ── 16. IsClean false when unwanted artifact present ─────────────────────

    [Fact]
    public void VerifyArchive_NotClean_WhenUnwantedArtifactPresent()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("dirty-content");
        ProvisionArtifact("unwanted", "Dirty.chd", content);
        WriteArchiveFile("Dirty.chd", content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());
        Assert.False(plan.IsClean);
    }

    // ── 17. IsClean unaffected by absent DB artifacts ────────────────────────

    [Fact]
    public void VerifyArchive_IsClean_UnaffectedByAbsentDbArtifacts()
    {
        // DB artifacts with no physical files must NOT make the archive "dirty".
        ProvisionArtifact("present", "Absent1.chd");
        ProvisionArtifact("present", "Absent2.chd");

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());

        Assert.Equal(2, plan.AbsentFromArchiveCount);
        Assert.True(plan.IsClean, "Absent DB artifacts must not affect IsClean");
    }

    // ── Helpers for redundancy tests ──────────────────────────────────────────

    private string CreateVolumeDir(string label)
    {
        var dir = Path.Combine(_tmp, "volumes", label);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IReadOnlyDictionary<string, AssignedVolumeInfo> SingleAssignment(
        string daId, string volId, string label, string? rootPath)
        => new Dictionary<string, AssignedVolumeInfo>(StringComparer.Ordinal)
            { [daId] = new AssignedVolumeInfo(volId, label, rootPath) };

    // ── 18. RedundantArchiveCopy when volume is reachable and hash matches ────

    [Fact]
    public void VerifyArchive_RedundantCopyDetected_WhenAssignedVolumeReachableAndHashMatches()
    {
        var content   = System.Text.Encoding.UTF8.GetBytes("redundant-content");
        var (_, daId, sha1) = ProvisionArtifact("present", "Redundant.chd", content);
        WriteArchiveFile("Redundant.chd", content);

        var volumeDir = CreateVolumeDir("TestVol");
        File.WriteAllBytes(Path.Combine(volumeDir, "Redundant.chd"), content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore(),
            assignedVolumes: SingleAssignment(daId, "vol-1", "TestVol", volumeDir));

        Assert.Equal(1, plan.RedundantCopies);
        Assert.Equal(0, plan.VolumeUnavailableWarnings);
        var entry = plan.Entries.Single(e => e.DerivedArtifactId == daId);
        Assert.Equal(LocalArchiveClass.RedundantArchiveCopy, entry.Classification);
        Assert.True(entry.IsRepairable);
        Assert.Equal("TestVol", entry.AssignedVolumeLabel);
        Assert.NotNull(entry.VolumeFilePath);
    }

    // ── 19. AssignedVolumeUnavailable when volume root is null ───────────────

    [Fact]
    public void VerifyArchive_AssignedVolumeUnavailable_WhenVolumeRootIsNull()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("unavailable-volume-content");
        var (_, daId, _) = ProvisionArtifact("present", "NoVol.chd", content);
        WriteArchiveFile("NoVol.chd", content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore(),
            assignedVolumes: SingleAssignment(daId, "vol-gone", "MissingVol", null));

        Assert.Equal(0, plan.RedundantCopies);
        Assert.Equal(1, plan.VolumeUnavailableWarnings);
        var entry = plan.Entries.Single(e => e.DerivedArtifactId == daId);
        Assert.Equal(LocalArchiveClass.AssignedVolumeUnavailable, entry.Classification);
        Assert.False(entry.IsRepairable);
        Assert.Equal("MissingVol", entry.AssignedVolumeLabel);
        Assert.False(plan.IsClean);
    }

    // ── 20. WantedArchiveOk when no assignedVolumes map is passed ────────────

    [Fact]
    public void VerifyArchive_WantedArchiveOk_WhenNoAssignedVolumesProvided()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("no-map-content");
        var (_, daId, _) = ProvisionArtifact("present", "NoMap.chd", content);
        WriteArchiveFile("NoMap.chd", content);

        // No assignedVolumes passed → falls through to WantedArchiveOk.
        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore());

        Assert.Equal(1, plan.WantedOk);
        Assert.Equal(0, plan.RedundantCopies);
        var entry = plan.Entries.Single(e => e.DerivedArtifactId == daId);
        Assert.Equal(LocalArchiveClass.WantedArchiveOk, entry.Classification);
    }

    // ── 21. Repair: redundant archive moved to incoming-skip after re-verify ──

    [Fact]
    public void VerifyArchive_RepairRedundant_MovesArchiveAfterVerifyingVolumeCopy()
    {
        var content   = System.Text.Encoding.UTF8.GetBytes("move-after-verify");
        var (_, daId, _) = ProvisionArtifact("present", "MovedAfterVerify.chd", content);
        var archivePath  = Path.Combine(ArchiveDir, "MovedAfterVerify.chd");
        WriteArchiveFile("MovedAfterVerify.chd", content);

        var volumeDir  = CreateVolumeDir("RepairVol");
        var volFile    = Path.Combine(volumeDir, "MovedAfterVerify.chd");
        File.WriteAllBytes(volFile, content);

        var av   = SingleAssignment(daId, "vol-r", "RepairVol", volumeDir);
        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore(), assignedVolumes: av);
        Assert.Equal(1, plan.RedundantCopies);

        var result = MakeService().Repair(plan, OpenStore());

        Assert.True(result.Success);
        Assert.Equal(1, result.MovedToSkip);
        Assert.False(File.Exists(archivePath), "Archive should have moved to incoming-skip");
        Assert.True(File.Exists(Path.Combine(SkipDir, "MovedAfterVerify.chd")),
            "Should be in incoming-skip/<platform>/");
        Assert.True(File.Exists(volFile), "Volume copy must remain untouched");
    }

    // ── 22. Repair: archive kept when volume copy is missing ─────────────────

    [Fact]
    public void VerifyArchive_RepairRedundant_DoesNotMoveIfVolumeCopyMissing()
    {
        var content   = System.Text.Encoding.UTF8.GetBytes("vol-missing-content");
        var (_, daId, _) = ProvisionArtifact("present", "VolMissing.chd", content);
        var archivePath  = Path.Combine(ArchiveDir, "VolMissing.chd");
        WriteArchiveFile("VolMissing.chd", content);

        var volumeDir = CreateVolumeDir("VolMissingVol");
        // Volume copy intentionally NOT written.

        var av   = SingleAssignment(daId, "vol-m", "VolMissingVol", volumeDir);
        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore(), assignedVolumes: av);
        Assert.Equal(1, plan.RedundantCopies);

        var progressActions = new List<string>();
        var progress = new DelegateProgress<LocalArchiveVerifyProgress>(p =>
            progressActions.Add(p.Action));

        var result = MakeService().Repair(plan, OpenStore(), progress);

        Assert.True(result.Success);
        Assert.Equal(0, result.MovedToSkip);
        Assert.True(File.Exists(archivePath), "Archive must remain when volume copy is missing");
        Assert.Contains("archive-volume-copy-missing", progressActions);
    }

    // ── 23. Repair: archive kept when volume copy is corrupt ─────────────────

    [Fact]
    public void VerifyArchive_RepairRedundant_DoesNotMoveIfVolumeCopyCorrupt()
    {
        var content   = System.Text.Encoding.UTF8.GetBytes("corrupt-vol-content");
        var corrupted = System.Text.Encoding.UTF8.GetBytes("CORRUPT");
        var (_, daId, _) = ProvisionArtifact("present", "VolCorrupt.chd", content);
        var archivePath  = Path.Combine(ArchiveDir, "VolCorrupt.chd");
        WriteArchiveFile("VolCorrupt.chd", content);

        var volumeDir = CreateVolumeDir("CorruptVol");
        File.WriteAllBytes(Path.Combine(volumeDir, "VolCorrupt.chd"), corrupted);

        var av   = SingleAssignment(daId, "vol-c", "CorruptVol", volumeDir);
        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore(), assignedVolumes: av);

        var progressActions = new List<string>();
        var progress = new DelegateProgress<LocalArchiveVerifyProgress>(p =>
            progressActions.Add(p.Action));

        var result = MakeService().Repair(plan, OpenStore(), progress);

        Assert.True(result.Success);
        Assert.Equal(0, result.MovedToSkip);
        Assert.True(File.Exists(archivePath), "Archive must remain when volume copy is corrupt");
        Assert.Contains("archive-volume-copy-missing", progressActions);
    }

    // ── 24. Repair: DB rows are NOT modified for redundant copies ────────────

    [Fact]
    public void VerifyArchive_RepairRedundant_DoesNotModifyDbRows()
    {
        var content  = System.Text.Encoding.UTF8.GetBytes("keep-db-rows");
        var (relId, daId, _) = ProvisionArtifact("present", "KeepDb.chd", content);
        WriteArchiveFile("KeepDb.chd", content);

        var volumeDir = CreateVolumeDir("DbSafeVol");
        File.WriteAllBytes(Path.Combine(volumeDir, "KeepDb.chd"), content);

        var av   = SingleAssignment(daId, "vol-db", "DbSafeVol", volumeDir);
        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore(), assignedVolumes: av);
        MakeService().Repair(plan, OpenStore());

        // DA row must remain.
        var infos = OpenStore().GetAllArchiveArtifactInfos();
        Assert.Contains(infos, a => a.DerivedArtifactId == daId);

        // Release row must remain with original status.
        var rels = OpenStore().LoadReleasesByDatLine(DatLineId);
        Assert.Contains(rels, r => r.Id == relId && r.Status == "present");

        // Removed count must be zero.
        Assert.Equal(0, plan.Entries
            .Where(e => e.Classification == LocalArchiveClass.RedundantArchiveCopy)
            .Count(e => !e.IsRepairable));
    }

    // ── 25. IsClean false when VolumeUnavailableWarnings > 0 ─────────────────

    [Fact]
    public void VerifyArchive_IsClean_FalseWhenVolumeUnavailableWarnings()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("unavailable-for-clean");
        var (_, daId, _) = ProvisionArtifact("present", "Unavail.chd", content);
        WriteArchiveFile("Unavail.chd", content);

        var plan = MakeService().Verify(PlatformId, DatLineId, OpenStore(),
            assignedVolumes: SingleAssignment(daId, "vol-na", "NoVol", null));

        Assert.Equal(1, plan.VolumeUnavailableWarnings);
        Assert.False(plan.IsClean, "IsClean must be false when a volume is unavailable");
    }
}

// ── Test helper: synchronous IProgress<T> ────────────────────────────────────

internal sealed class DelegateProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public DelegateProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}
