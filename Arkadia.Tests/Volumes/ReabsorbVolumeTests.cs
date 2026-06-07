using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Integration tests for Reabsorb Volume safety and correctness.
/// SimulateReabsorb mirrors the per-artifact loop and state machine in
/// OnReabsorbVolume/the Task.Run block exactly, with an optional
/// failAtIndex parameter to inject a mid-process abort.
/// No UI dependencies.
/// </summary>
public sealed class ReabsorbVolumeTests : IDisposable
{
    // _tempRoot is both the "appRoot" and the parent of all subdirectories.
    private readonly string _tempRoot;
    private readonly string _catalogDir;
    private readonly string _datDir;
    private readonly string _volumesDir;

    public ReabsorbVolumeTests()
    {
        _tempRoot   = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _catalogDir = Path.Combine(_tempRoot, "data");
        _datDir     = Path.Combine(_tempRoot, "dat");
        _volumesDir = Path.Combine(_tempRoot, "volumes");
        Directory.CreateDirectory(_catalogDir);
        Directory.CreateDirectory(_datDir);
        Directory.CreateDirectory(_volumesDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb      = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var sanitized = sb.ToString().Trim('_', ' ');
        return sanitized.Length > 0 ? sanitized : "release";
    }

    private static string FileSha1(byte[] bytes)
        => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static string FileSha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    // ── Artifact spec ─────────────────────────────────────────────────────────

    private sealed record ArtifactSpec(
        string DerivedArtifactId,
        string ReleaseName,
        string FileName,
        string Sha1,
        byte[] Content,
        /// <summary>Relative path stored in derived_artifacts (forward slashes).</summary>
        string RelativePath);

    // ── Provision ─────────────────────────────────────────────────────────────

    private (CatalogService Catalog, DatLineStore Store, VolumeRecord Volume, List<ArtifactSpec> Specs)
        Provision(string label, string volumeStatus, int artifactCount,
                  string platformId = "platform-test", string dlId = "dl-test",
                  int lostCount = 0)
    {
        var catalog = new CatalogService(_catalogDir);
        var dbPath  = Path.Combine(_datDir, $"{label}.db");
        var store   = new DatLineStore(dbPath);
        var volId   = Guid.NewGuid().ToString("N");

        var volume = new VolumeRecord
        {
            Id               = volId,
            Label            = label,
            PlatformId       = platformId,
            DatLineId        = dlId,
            Status           = volumeStatus,
            Health           = "ok",
            PlannedSizeBytes = 1024L * 1024,
            ActualSizeBytes  = 0,
            CreatedAt        = DateTime.UtcNow,
        };
        catalog.SaveVolume(volume);

        int total = artifactCount + lostCount;
        var rawItems = Enumerable.Range(0, total).Select(i =>
        {
            var relName   = $"Release {i}";
            var fileName  = $"game_{i}.rom";
            var content   = System.Text.Encoding.UTF8.GetBytes($"content-seed-{i}");
            var sha1      = FileSha1(content);
            var cik       = $"sha1:{sha1}";
            var releaseId = Guid.NewGuid().ToString("N");
            var relPath   = $"archive/{platformId}/{dlId}/{SafeFileName(relName)}/{fileName}";
            return (relName, fileName, content, sha1, cik, releaseId, relPath);
        }).ToList();

        store.SaveReleases(rawItems.Select(r => new ReleaseRecord
        {
            Id = r.releaseId, DatLineId = dlId, Name = r.relName,
            Status = "missing", ReleaseContentKey = r.cik,
        }).ToList());

        var specs = new List<ArtifactSpec>(total);
        for (int i = 0; i < total; i++)
        {
            var (relName, fileName, content, sha1, cik, releaseId, relPath) = rawItems[i];

            store.EnsureContentIdentity(new ContentIdentityRecord
            {
                ContentIdentityKey = cik, DatSha1 = sha1,
                DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow,
            });
            store.SaveReleaseContentLink(new ReleaseContentLinkRecord
            {
                Id = Guid.NewGuid().ToString("N"), ReleaseId = releaseId,
                ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow,
            });
            var daId = store.IngestDerivedArtifact(
                cik, "", "no_compression", fileName, relPath, content.Length, sha1);

            // last `lostCount` artifacts get va.Status = "lost"
            bool isLost    = i >= artifactCount;
            var  vaStatus  = isLost ? "lost" : "present_in_final";

            catalog.SaveVolumeArtifact(new VolumeArtifactRecord
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = volId, DatLineId = dlId,
                DerivedArtifactId = daId, ContentIdentityKey = cik,
                Status = vaStatus, AddedAtUtc = DateTime.UtcNow,
            });

            specs.Add(new ArtifactSpec(daId, relName, fileName, sha1, content, relPath));
        }

        return (catalog, store, volume, specs);
    }

    // ── SimulateReabsorb ──────────────────────────────────────────────────────
    // Mirrors OnReabsorbVolume state machine. `failAtIndex` injects a verify
    // abort after the given number of successful transfers (default = no abort).

    private sealed record ReabsorbResult(
        List<string> SuccessDaIds,
        bool FullSuccess,
        string? AbortReason);

    private ReabsorbResult SimulateReabsorb(
        CatalogService catalog, DatLineStore store,
        VolumeRecord volume, string volumeRoot,
        int failAtIndex = int.MaxValue)
    {
        // STEP 1B: separate lost from active
        var assignments     = catalog.GetVolumeArtifacts(volume.Id);
        var activeAssign    = assignments.Where(va => va.Status != "lost").ToList();

        // Build infos from store
        var daIds      = activeAssign.Select(va => va.DerivedArtifactId).ToList();
        var buildInfos = store.GetArtifactBuildInfos(daIds);
        var infoById   = buildInfos.ToDictionary(b => b.DerivedArtifactId, StringComparer.Ordinal);

        // Candidates: only physically present on volume
        var appRoot = _tempRoot;
        var candidates = activeAssign
            .Where(va => infoById.ContainsKey(va.DerivedArtifactId))
            .Select(va =>
            {
                var info = infoById[va.DerivedArtifactId];
                var src  = Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, info.FileName);
                var dst  = Path.Combine(appRoot,
                    info.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                return (Info: info, Src: src, Dst: dst);
            })
            .Where(x => File.Exists(x.Src))
            .ToList();

        string? abortReason  = null;
        var     successDaIds = new List<string>();

        // Per-artifact: copy → verify → delete (strict per-file order)
        foreach (var (info, src, dst) in candidates)
        {
            // Inject failure after `failAtIndex` successes
            if (successDaIds.Count == failAtIndex)
            {
                abortReason = $"injected-failure: {info.FileName}";
                break;
            }

            bool dstExists = File.Exists(dst);

            if (dstExists)
            {
                // CASE A: already in local archive — SHA1 verify
                var sha1Dst = FileSha1(dst);
                var sha1Src = FileSha1(src);

                if (string.Equals(sha1Dst, sha1Src, StringComparison.OrdinalIgnoreCase))
                {
                    // Valid — delete volume copy, mark success
                    File.Delete(src);
                    successDaIds.Add(info.DerivedArtifactId);
                    continue;
                }

                // Local copy invalid — fall through to CASE B
            }

            // CASE B: copy from volume → local archive
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);

            var sha1S = FileSha1(src);
            var sha1D = FileSha1(dst);

            if (!string.Equals(sha1S, sha1D, StringComparison.OrdinalIgnoreCase))
            {
                abortReason = $"SHA1 mismatch: {info.FileName}";
                break;
            }

            File.Delete(src);
            successDaIds.Add(info.DerivedArtifactId);
        }

        bool fullSuccess = abortReason is null;

        // DB updates — always apply for successfully transferred artifacts
        if (successDaIds.Count > 0)
        {
            store.BatchUpdateDerivedArtifactStatus(successDaIds, "present");
            store.RecalculateReleaseStatusForArtifacts(successDaIds);
        }

        if (fullSuccess)
        {
            // Full success: try to remove volume directory, then delete volume from DB
            try
            {
                if (Directory.Exists(volumeRoot))
                {
                    foreach (var dir in Directory.GetDirectories(
                                 volumeRoot, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.Length))
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                            Directory.Delete(dir);
                    }
                    if (!Directory.EnumerateFileSystemEntries(volumeRoot).Any())
                        Directory.Delete(volumeRoot);
                }
            }
            catch { /* non-fatal */ }

            catalog.DeleteVolume(volume.Id);
        }
        else
        {
            // Partial success: remove only transferred artifact mappings; keep volume
            if (successDaIds.Count > 0)
                catalog.RemoveVolumeArtifacts(volume.Id, successDaIds);
        }

        return new ReabsorbResult(successDaIds, fullSuccess, abortReason);
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    /// <summary>Writes the artifact file at its flat volume path.</summary>
    private static void SeedOnVolume(ArtifactSpec s, string volumeRoot)
    {
        Directory.CreateDirectory(volumeRoot);
        var path = Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, s.FileName);
        File.WriteAllBytes(path, s.Content);
    }

    /// <summary>Writes the artifact file at its local-archive (dst) path.</summary>
    private void SeedInArchive(ArtifactSpec s, string platformId, string dlId)
    {
        var dir = Path.Combine(_tempRoot, "archive", platformId, dlId, SafeFileName(s.ReleaseName));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, s.FileName), s.Content);
    }

    private static string VolumeSrcPath(ArtifactSpec s, string volumeRoot)
        => Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(volumeRoot, s.FileName);

    private string LocalArchivePath(ArtifactSpec s)
        => Path.Combine(_tempRoot,
            s.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 1 — Partial failure safety
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PartialFailure_AlreadyTransferred_AreInLocalArchive()
    {
        var (catalog, store, volume, specs) = Provision("vol-pf-archive", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-pf-archive");

        foreach (var s in specs) SeedOnVolume(s, root);

        // Abort after 1 successful transfer
        SimulateReabsorb(catalog, store, volume, root, failAtIndex: 1);

        // specs[0] was transferred successfully → must exist in local archive
        Assert.True(File.Exists(LocalArchivePath(specs[0])),
            "transferred artifact must be present in local archive");
    }

    [Fact]
    public void PartialFailure_AlreadyTransferred_MarkedPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-pf-status", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-pf-status");
        foreach (var s in specs) SeedOnVolume(s, root);

        var result = SimulateReabsorb(catalog, store, volume, root, failAtIndex: 1);

        var derived = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", derived[specs[0].DerivedArtifactId]);
    }

    [Fact]
    public void PartialFailure_RemainingArtifacts_StillOnVolume()
    {
        var (catalog, store, volume, specs) = Provision("vol-pf-remain", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-pf-remain");
        foreach (var s in specs) SeedOnVolume(s, root);

        // Abort after 1 transfer — specs[1] and specs[2] must stay on volume
        SimulateReabsorb(catalog, store, volume, root, failAtIndex: 1);

        Assert.True(File.Exists(VolumeSrcPath(specs[1], root)),
            "un-transferred artifact must remain on volume");
        Assert.True(File.Exists(VolumeSrcPath(specs[2], root)),
            "un-transferred artifact must remain on volume");
    }

    [Fact]
    public void PartialFailure_VolumeRecordNotDeleted()
    {
        var (catalog, store, volume, specs) = Provision("vol-pf-nodelete", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-pf-nodelete");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root, failAtIndex: 1);

        var stillExists = catalog.GetVolumes().Any(v => v.Id == volume.Id);
        Assert.True(stillExists, "volume record must NOT be deleted on partial failure");
    }

    [Fact]
    public void PartialFailure_TransferredArtifacts_MappingsRemovedFromVolume()
    {
        var (catalog, store, volume, specs) = Provision("vol-pf-map", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-pf-map");
        foreach (var s in specs) SeedOnVolume(s, root);

        var result = SimulateReabsorb(catalog, store, volume, root, failAtIndex: 1);

        var remaining = catalog.GetVolumeArtifacts(volume.Id)
                               .Select(va => va.DerivedArtifactId).ToList();

        // specs[0] was transferred → its mapping must be removed
        Assert.DoesNotContain(specs[0].DerivedArtifactId, remaining);
    }

    [Fact]
    public void PartialFailure_RemainingArtifacts_MappingsRetained()
    {
        var (catalog, store, volume, specs) = Provision("vol-pf-retain", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-pf-retain");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root, failAtIndex: 1);

        var remaining = catalog.GetVolumeArtifacts(volume.Id)
                               .Select(va => va.DerivedArtifactId).ToList();

        // specs[1] and specs[2] were NOT transferred → mappings must stay
        Assert.Contains(specs[1].DerivedArtifactId, remaining);
        Assert.Contains(specs[2].DerivedArtifactId, remaining);
    }

    [Fact]
    public void PartialFailure_AbortReasonSet_FullSuccessIsFalse()
    {
        var (catalog, store, volume, specs) = Provision("vol-pf-reason", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-pf-reason");
        foreach (var s in specs) SeedOnVolume(s, root);

        var result = SimulateReabsorb(catalog, store, volume, root, failAtIndex: 1);

        Assert.False(result.FullSuccess);
        Assert.NotNull(result.AbortReason);
    }

    [Fact]
    public void PartialFailure_ZeroSuccess_VolumeIntact_NoMappingChanges()
    {
        var (catalog, store, volume, specs) = Provision("vol-pf-zero", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-pf-zero");
        foreach (var s in specs) SeedOnVolume(s, root);

        // Abort before any transfer
        SimulateReabsorb(catalog, store, volume, root, failAtIndex: 0);

        Assert.Contains(catalog.GetVolumes(), v => v.Id == volume.Id);

        var remaining = catalog.GetVolumeArtifacts(volume.Id);
        Assert.Equal(2, remaining.Count);

        // Both files still on volume
        Assert.True(File.Exists(VolumeSrcPath(specs[0], root)));
        Assert.True(File.Exists(VolumeSrcPath(specs[1], root)));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 2 — Volume deletion condition
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FullSuccess_VolumeRecordDeleted()
    {
        var (catalog, store, volume, specs) = Provision("vol-del-full", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-del-full");
        foreach (var s in specs) SeedOnVolume(s, root);

        var result = SimulateReabsorb(catalog, store, volume, root);

        Assert.True(result.FullSuccess);
        Assert.False(catalog.GetVolumes().Any(v => v.Id == volume.Id),
            "volume record must be deleted after full success");
    }

    [Fact]
    public void FullSuccess_VolumeArtifactMappingsDeleted()
    {
        var (catalog, store, volume, specs) = Provision("vol-del-va", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-del-va");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root);

        // After DeleteVolume, no volume_artifacts rows remain
        // (DeleteVolume also drops volume_locations and volume_artifacts)
        var allVA = catalog.GetAllVolumeArtifacts()
                           .Where(va => va.VolumeId == volume.Id).ToList();
        Assert.Empty(allVA);
    }

    [Fact]
    public void FullSuccess_VolumeDirectoryRemoved()
    {
        var (catalog, store, volume, specs) = Provision("vol-del-dir", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-del-dir");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root);

        Assert.False(Directory.Exists(root),
            "volume directory must be removed after full success");
    }

    [Fact]
    public void PartialSuccess_VolumeRecordKept()
    {
        var (catalog, store, volume, specs) = Provision("vol-del-partial", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-del-partial");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root, failAtIndex: 2);

        Assert.Contains(catalog.GetVolumes(), v => v.Id == volume.Id);
    }

    [Fact]
    public void SingleArtifact_FullSuccess_VolumeDeleted()
    {
        var (catalog, store, volume, specs) = Provision("vol-del-single", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-del-single");
        SeedOnVolume(specs[0], root);

        var result = SimulateReabsorb(catalog, store, volume, root);

        Assert.True(result.FullSuccess);
        Assert.DoesNotContain(catalog.GetVolumes(), v => v.Id == volume.Id);
    }

    [Fact]
    public void SingleArtifact_Failure_VolumeRetained()
    {
        var (catalog, store, volume, specs) = Provision("vol-del-single-fail", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-del-single-fail");
        SeedOnVolume(specs[0], root);

        var result = SimulateReabsorb(catalog, store, volume, root, failAtIndex: 0);

        Assert.False(result.FullSuccess);
        Assert.Contains(catalog.GetVolumes(), v => v.Id == volume.Id);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 3 — Mapping correctness
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FullSuccess_AllArtifactsMarkedPresent_InDatLineStore()
    {
        var (catalog, store, volume, specs) = Provision("vol-map-present", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-map-present");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root);

        var derived = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specs, s => Assert.Equal("present", derived[s.DerivedArtifactId]));
    }

    [Fact]
    public void FullSuccess_ReleasesMarkedPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-map-rel", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-map-rel");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root);

        var releases = store.LoadReleases();
        Assert.All(releases, r => Assert.Equal("present", r.Status));
    }

    [Fact]
    public void FullSuccess_NoMappingsReferenceDeletedVolume()
    {
        var (catalog, store, volume, specs) = Provision("vol-map-orphan", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-map-orphan");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root);

        // Integrity: no volume_artifacts row should reference a non-existent volume
        var orphans = catalog.GetOrphanVolumeArtifactsByVolumeId();
        Assert.Empty(orphans);
    }

    [Fact]
    public void PartialSuccess_OnlyTransferredMappingsRemoved()
    {
        var (catalog, store, volume, specs) = Provision("vol-map-partial", "present", 4);
        var root = Path.Combine(_volumesDir, "vol-map-partial");
        foreach (var s in specs) SeedOnVolume(s, root);

        // Transfer 2, abort before 3rd
        SimulateReabsorb(catalog, store, volume, root, failAtIndex: 2);

        var remaining = catalog.GetVolumeArtifacts(volume.Id)
                               .Select(va => va.DerivedArtifactId)
                               .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(specs[0].DerivedArtifactId, remaining);
        Assert.DoesNotContain(specs[1].DerivedArtifactId, remaining);
        Assert.Contains(specs[2].DerivedArtifactId, remaining);
        Assert.Contains(specs[3].DerivedArtifactId, remaining);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 4 — Duplicate handling (CASE A: already in local archive)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CaseA_ValidLocalCopy_NoDuplicateCreated()
    {
        var (catalog, store, volume, specs) = Provision("vol-dup-nodup", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-dup-nodup");
        SeedOnVolume(specs[0], root);
        SeedInArchive(specs[0], "platform-test", "dl-test");  // already in archive

        SimulateReabsorb(catalog, store, volume, root);

        // Exactly one copy in archive (no duplication occurred)
        var archivePath = LocalArchivePath(specs[0]);
        Assert.True(File.Exists(archivePath));
        // Content must be the original (not corrupted by spurious re-copy)
        Assert.Equal(specs[0].Sha1, FileSha1(archivePath));
    }

    [Fact]
    public void CaseA_ValidLocalCopy_VolumeFileDeletededAfterVerify()
    {
        var (catalog, store, volume, specs) = Provision("vol-dup-del", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-dup-del");
        SeedOnVolume(specs[0], root);
        SeedInArchive(specs[0], "platform-test", "dl-test");

        SimulateReabsorb(catalog, store, volume, root);

        Assert.False(File.Exists(VolumeSrcPath(specs[0], root)),
            "volume file must be deleted after CASE A verify success");
    }

    [Fact]
    public void CaseA_ValidLocalCopy_ArtifactMarkedPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-dup-stat", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-dup-stat");
        SeedOnVolume(specs[0], root);
        SeedInArchive(specs[0], "platform-test", "dl-test");

        SimulateReabsorb(catalog, store, volume, root);

        var derived = store.GetDerivedArtifacts().Single(d => d.Id == specs[0].DerivedArtifactId);
        Assert.Equal("present", derived.Status);
    }

    [Fact]
    public void CaseA_InvalidLocalCopy_FallsBackToCopy_OverwritesWithCorrectContent()
    {
        var (catalog, store, volume, specs) = Provision("vol-dup-invalid", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-dup-invalid");
        SeedOnVolume(specs[0], root);

        // Write WRONG content to archive path (simulates a corrupted local copy)
        var archivePath = LocalArchivePath(specs[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        File.WriteAllBytes(archivePath, System.Text.Encoding.UTF8.GetBytes("CORRUPT_LOCAL_COPY"));

        var result = SimulateReabsorb(catalog, store, volume, root);

        // CASE B ran (overwrite: true) — archive must now contain the correct bytes
        Assert.True(result.FullSuccess);
        Assert.Equal(specs[0].Sha1, FileSha1(archivePath));
    }

    [Fact]
    public void CaseA_MixedAlreadyLocal_CountedCorrectly()
    {
        // 3 artifacts: specs[0] already in archive (CASE A), specs[1-2] need copy (CASE B)
        var (catalog, store, volume, specs) = Provision("vol-dup-mixed", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-dup-mixed");
        foreach (var s in specs) SeedOnVolume(s, root);
        SeedInArchive(specs[0], "platform-test", "dl-test");

        var result = SimulateReabsorb(catalog, store, volume, root);

        Assert.True(result.FullSuccess);
        Assert.Equal(3, result.SuccessDaIds.Count);

        // All archive paths must exist with correct content
        foreach (var s in specs)
            Assert.Equal(s.Sha1, FileSha1(LocalArchivePath(s)));
    }

    [Fact]
    public void CaseA_AlreadyLocal_DoesNotDeleteUnrelatedLocalFiles()
    {
        var (catalog, store, volume, specs) = Provision("vol-dup-noevict", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-dup-noevict");
        SeedOnVolume(specs[0], root);
        SeedInArchive(specs[0], "platform-test", "dl-test");

        // Place an unrelated file in the same archive directory
        var archiveDir  = Path.GetDirectoryName(LocalArchivePath(specs[0]))!;
        var unrelatedPath = Path.Combine(archiveDir, "unrelated.rom");
        File.WriteAllBytes(unrelatedPath, System.Text.Encoding.UTF8.GetBytes("OTHER_FILE"));

        SimulateReabsorb(catalog, store, volume, root);

        Assert.True(File.Exists(unrelatedPath),
            "unrelated archive file must not be deleted");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CHECK 5 — Lost artifact handling
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void LostArtifacts_ExcludedFromCandidates_NoError()
    {
        // 2 active + 2 lost artifacts
        var (catalog, store, volume, specs) = Provision("vol-lost-excl", "present",
            artifactCount: 2, lostCount: 2);
        var root = Path.Combine(_volumesDir, "vol-lost-excl");

        // Seed only the active artifacts on the volume
        SeedOnVolume(specs[0], root);
        SeedOnVolume(specs[1], root);
        // specs[2] and specs[3] are LOST — not seeded, not expected

        var result = SimulateReabsorb(catalog, store, volume, root);

        Assert.True(result.FullSuccess);
        Assert.Equal(2, result.SuccessDaIds.Count);

        // Lost artifact IDs must not appear in successDaIds
        Assert.DoesNotContain(specs[2].DerivedArtifactId, result.SuccessDaIds);
        Assert.DoesNotContain(specs[3].DerivedArtifactId, result.SuccessDaIds);
    }

    [Fact]
    public void LostArtifacts_DoNotBlockReabsorbOfActiveOnes()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-noblock", "present",
            artifactCount: 2, lostCount: 3);
        var root = Path.Combine(_volumesDir, "vol-lost-noblock");

        SeedOnVolume(specs[0], root);
        SeedOnVolume(specs[1], root);

        var result = SimulateReabsorb(catalog, store, volume, root);

        // Reabsorb completes fully for the 2 active artifacts
        Assert.True(result.FullSuccess);
    }

    [Fact]
    public void LostArtifacts_VolumeDeletedIfAllActiveTransferred()
    {
        // 1 active + 1 lost: after active is transferred, volume must be deleted
        var (catalog, store, volume, specs) = Provision("vol-lost-del", "present",
            artifactCount: 1, lostCount: 1);
        var root = Path.Combine(_volumesDir, "vol-lost-del");

        SeedOnVolume(specs[0], root);

        var result = SimulateReabsorb(catalog, store, volume, root);

        Assert.True(result.FullSuccess);
        Assert.False(catalog.GetVolumes().Any(v => v.Id == volume.Id),
            "volume must be deleted when all active artifacts are transferred, even with lost ones");
    }

    [Fact]
    public void LostArtifacts_MappingsRemovedByDeleteVolume_OnFullSuccess()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-vaclean", "present",
            artifactCount: 1, lostCount: 2);
        var root = Path.Combine(_volumesDir, "vol-lost-vaclean");
        SeedOnVolume(specs[0], root);

        SimulateReabsorb(catalog, store, volume, root);

        // DeleteVolume removes ALL volume_artifacts (including lost ones)
        var orphans = catalog.GetOrphanVolumeArtifactsByVolumeId();
        Assert.Empty(orphans);
    }

    [Fact]
    public void LostArtifacts_OnlyOnVolume_NoActiveArtifacts_CandidatesEmpty()
    {
        // 0 active, 2 lost — candidates will be empty; nothing is reabsorbed.
        var (catalog, store, volume, specs) = Provision("vol-lost-only", "present",
            artifactCount: 0, lostCount: 2);
        var root = Path.Combine(_volumesDir, "vol-lost-only");
        Directory.CreateDirectory(root);

        // Guard: candidates list is empty (no active VA)
        var assignments  = catalog.GetVolumeArtifacts(volume.Id);
        var activeAssign = assignments.Where(va => va.Status != "lost").ToList();
        Assert.Empty(activeAssign);

        // Confirm: both specs are lost
        Assert.All(assignments, va => Assert.Equal("lost", va.Status));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Extra safety checks
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Reabsorb_DoesNotModifyUnrelatedVolumes()
    {
        // Two separate volumes in the same catalog; reabsorbing one must not touch the other.
        var (catalog, storeA, volA, specsA) = Provision("vol-iso-A", "present", 2);
        var (catalog2, storeB, volB, specsB) = Provision("vol-iso-B", "present", 2);

        var rootA = Path.Combine(_volumesDir, "vol-iso-A");
        foreach (var s in specsA) SeedOnVolume(s, rootA);

        SimulateReabsorb(catalog, storeA, volA, rootA);

        // Volume B must still exist untouched in catalog2
        Assert.Contains(catalog2.GetVolumes(), v => v.Id == volB.Id);
        var vaB = catalog2.GetVolumeArtifacts(volB.Id);
        Assert.Equal(2, vaB.Count);
    }

    [Fact]
    public void Reabsorb_LocalArchiveFiles_HaveCorrectSha1AfterTransfer()
    {
        var (catalog, store, volume, specs) = Provision("vol-sha1-check", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-sha1-check");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root);

        foreach (var s in specs)
        {
            var archivePath = LocalArchivePath(s);
            Assert.True(File.Exists(archivePath));
            Assert.Equal(s.Sha1, FileSha1(archivePath));
        }
    }

    [Fact]
    public void Reabsorb_VolumeFilesRemovedAfterFullSuccess()
    {
        var (catalog, store, volume, specs) = Provision("vol-voldel-check", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-voldel-check");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root);

        foreach (var s in specs)
            Assert.False(File.Exists(VolumeSrcPath(s, root)),
                $"volume file for {s.FileName} must be deleted after full reabsorb");
    }

    [Fact]
    public void Reabsorb_TransferredButAborted_SourceFileNotDeletedAfterAbort()
    {
        // Artifact 0 succeeds and is removed from volume.
        // Artifact 1 is the abort point — its src must still exist.
        var (catalog, store, volume, specs) = Provision("vol-abort-src", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-abort-src");
        foreach (var s in specs) SeedOnVolume(s, root);

        SimulateReabsorb(catalog, store, volume, root, failAtIndex: 1);

        // specs[0] was transferred → deleted from volume
        Assert.False(File.Exists(VolumeSrcPath(specs[0], root)));

        // specs[1] was the abort target → aborted before copy, so src untouched
        Assert.True(File.Exists(VolumeSrcPath(specs[1], root)));
        Assert.True(File.Exists(VolumeSrcPath(specs[2], root)));
    }
}
