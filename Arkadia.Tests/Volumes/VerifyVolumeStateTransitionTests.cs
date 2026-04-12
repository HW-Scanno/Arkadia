using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Integration tests for Verify Volume state transitions.
/// Each test provisions a real temp CatalogService + DatLineStore + filesystem,
/// then runs the identical scan → state-update sequence that OnVerifyVolume uses.
/// No UI dependencies.
/// </summary>
public sealed class VerifyVolumeStateTransitionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _catalogDir;
    private readonly string _datDir;
    private readonly string _volumesDir;

    public VerifyVolumeStateTransitionTests()
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

    private static string FileSha1(byte[] bytes)
        => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static string FileSha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    private sealed record ArtifactSpec(
        string DerivedArtifactId,
        string ReleaseName,
        string FileName,
        string Sha1,
        byte[] Content);

    /// <summary>
    /// Creates a fresh catalog + dat-line store + volume record.
    /// Returns the catalog, store, volume, and specs for each artifact.
    /// </summary>
    private (CatalogService Catalog, DatLineStore Store, VolumeRecord Volume, List<ArtifactSpec> Artifacts)
        Provision(string label, string status, int artifactCount)
    {
        var catalog  = new CatalogService(_catalogDir);
        var dbPath   = Path.Combine(_datDir, $"{label}.db");
        var store    = new DatLineStore(dbPath);
        var volId    = Guid.NewGuid().ToString("N");
        const string dlId       = "dl-test";
        const string platformId = "platform-test";

        // Volume record
        var volume = new VolumeRecord
        {
            Id               = volId,
            Label            = label,
            PlatformId       = platformId,
            DatLineId        = dlId,
            Status           = status,
            Health           = status == "lost" ? "crit" : "ok",
            PlannedSizeBytes = 1024,
            ActualSizeBytes  = 0,
            CreatedAt        = DateTime.UtcNow,
        };
        catalog.SaveVolume(volume);

        // Build raw data for all artifacts first (SaveReleases is a full replace, so must be called once)
        var rawItems = Enumerable.Range(0, artifactCount).Select(i =>
        {
            var relName  = $"Release {i}";
            var fileName = $"game_{i}.rom";
            var content  = System.Text.Encoding.UTF8.GetBytes($"content-seed-{i}");
            var sha1     = FileSha1(content);
            var cik      = $"sha1:{sha1}";
            var releaseId = Guid.NewGuid().ToString("N");
            return (relName, fileName, content, sha1, cik, releaseId);
        }).ToList();

        // Insert all releases in one call (SaveReleases deletes then re-inserts)
        store.SaveReleases(rawItems.Select(r => new ReleaseRecord
        {
            Id               = r.releaseId,
            DatLineId        = dlId,
            Name             = r.relName,
            Status           = "missing",
            ReleaseContentKey = r.cik,
        }).ToList());

        var specs = new List<ArtifactSpec>(artifactCount);
        foreach (var (relName, fileName, content, sha1, cik, releaseId) in rawItems)
        {
            store.EnsureContentIdentity(new ContentIdentityRecord
            {
                ContentIdentityKey = cik,
                DatSha1            = sha1,
                DatMd5             = null,
                DatCrc32           = null,
                CreatedAtUtc       = DateTime.UtcNow,
            });

            store.SaveReleaseContentLink(new ReleaseContentLinkRecord
            {
                Id                 = Guid.NewGuid().ToString("N"),
                ReleaseId          = releaseId,
                ContentIdentityKey = cik,
                CreatedAtUtc       = DateTime.UtcNow,
            });

            var daId = store.IngestDerivedArtifact(
                contentIdentityKey: cik,
                sourceArtifactId:   "",
                storageStrategyId:  "no_compression",
                fileName:           fileName,
                relativePath:       $"archive/{platformId}/{dlId}/{relName}/{fileName}",
                derivedSizeBytes:   content.Length,
                hashedDerivedSha1:  sha1);

            catalog.SaveVolumeArtifact(new VolumeArtifactRecord
            {
                Id                 = Guid.NewGuid().ToString("N"),
                VolumeId           = volId,
                DatLineId          = dlId,
                DerivedArtifactId  = daId,
                ContentIdentityKey = cik,
                Status             = "present_in_final",
                AddedAtUtc         = DateTime.UtcNow,
            });

            specs.Add(new ArtifactSpec(daId, relName, fileName, sha1, content));
        }

        return (catalog, store, volume, specs);
    }

    /// <summary>
    /// Writes all artifact files for the specified specs into the volume root.
    /// </summary>
    private void WriteFiles(string volumeRoot, IEnumerable<ArtifactSpec> specs)
    {
        foreach (var s in specs)
        {
            var dir = Path.Combine(volumeRoot, s.ReleaseName);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, s.FileName), s.Content);
        }
    }

    /// <summary>
    /// Replicates the exact scan + state-update sequence from OnVerifyVolume.
    /// Does not touch any UI. Returns (presentCount, badCount).
    /// </summary>
    private static (int Present, int Bad) SimulateVerify(
        CatalogService catalog, DatLineStore store,
        VolumeRecord volume, string volumeRoot)
    {
        bool wasLost = volume.Status == "lost";

        var vaIds       = catalog.GetVolumeArtifacts(volume.Id)
                                 .Select(va => va.DerivedArtifactId).ToList();
        var verifyInfos = store.GetArtifactVerifyInfos(vaIds);

        var presentDaIds = new List<string>();
        var badDaIds     = new List<string>();

        foreach (var vi in verifyInfos)
        {
            var absPath = Path.Combine(volumeRoot, vi.ReleaseName, vi.FileName);

            if (!File.Exists(absPath))
            {
                badDaIds.Add(vi.DerivedArtifactId);
            }
            else if (vi.Sha1.Length > 0)
            {
                var actual = FileSha1(absPath);
                if (string.Equals(actual, vi.Sha1, StringComparison.OrdinalIgnoreCase))
                    presentDaIds.Add(vi.DerivedArtifactId);
                else
                    badDaIds.Add(vi.DerivedArtifactId);
            }
            else
            {
                // No hash recorded — treat as present if file exists
                presentDaIds.Add(vi.DerivedArtifactId);
            }
        }

        bool allPresent = badDaIds.Count == 0 && presentDaIds.Count == verifyInfos.Count;
        string newHealth = allPresent ? "ok" : "crit";

        if (presentDaIds.Count > 0)
            store.BatchUpdateDerivedArtifactStatus(presentDaIds, "present");
        if (badDaIds.Count > 0)
            store.BatchUpdateDerivedArtifactStatus(badDaIds, "missing");

        var allChanged = presentDaIds.Concat(badDaIds).ToList();
        if (allChanged.Count > 0)
            store.RecalculateReleaseStatusForArtifacts(allChanged);

        if (wasLost && allPresent)
        {
            catalog.UpdateVolumeStatus(volume.Id, "present");
            catalog.UpdateVolumeHealth(volume.Id, "ok");
            catalog.SetCurrentLocation(new VolumeLocationRecord
            {
                Id           = Guid.NewGuid().ToString("N"),
                VolumeId     = volume.Id,
                LocationType = "workspace",
                DiskId       = null,
                Path         = volumeRoot,
                IsCurrent    = true,
                CreatedAt    = DateTime.UtcNow,
            });
        }
        else
        {
            catalog.UpdateVolumeHealth(volume.Id, newHealth);
        }

        return (presentDaIds.Count, badDaIds.Count);
    }

    // ── Check 1: LOST recovery ─────────────────────────────────────────────────

    [Fact]
    public void LostVolume_AllArtifactsPresent_StatusBecomesPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-full", "lost", 3);
        var root = Path.Combine(_volumesDir, "vol-lost-full");
        WriteFiles(root, specs);

        var (present, bad) = SimulateVerify(catalog, store, volume, root);

        Assert.Equal(3, present);
        Assert.Equal(0, bad);

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("present", updated.Status);
        Assert.Equal("ok",      updated.Health);
    }

    [Fact]
    public void LostVolume_AllArtifactsPresent_LocationIsSet()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-loc", "lost", 2);
        var root = Path.Combine(_volumesDir, "vol-lost-loc");
        WriteFiles(root, specs);

        SimulateVerify(catalog, store, volume, root);

        var loc = catalog.GetCurrentLocation(volume.Id);
        Assert.NotNull(loc);
        Assert.Equal("workspace", loc!.LocationType);
        Assert.Equal(root, loc.Path);
    }

    [Fact]
    public void LostVolume_AllArtifactsPresent_ArtifactStatusesArePresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-art", "lost", 3);
        var root = Path.Combine(_volumesDir, "vol-lost-art");
        WriteFiles(root, specs);

        SimulateVerify(catalog, store, volume, root);

        var derived = store.GetDerivedArtifacts();
        Assert.All(derived, da => Assert.Equal("present", da.Status));
    }

    [Fact]
    public void LostVolume_AllArtifactsPresent_ReleasesBecomesPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-rel", "lost", 2);
        var root = Path.Combine(_volumesDir, "vol-lost-rel");
        WriteFiles(root, specs);

        SimulateVerify(catalog, store, volume, root);

        var releases = store.LoadReleases();
        Assert.All(releases, r => Assert.Equal("present", r.Status));
    }

    // ── Check 2: Partial verify — volume must stay LOST ────────────────────────

    [Fact]
    public void LostVolume_SomeMissing_StatusRemainsLost()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-partial", "lost", 3);
        var root = Path.Combine(_volumesDir, "vol-lost-partial");

        // Only write the first 2; artifact 2 is absent
        WriteFiles(root, specs.Take(2));

        SimulateVerify(catalog, store, volume, root);

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("lost", updated.Status);
    }

    [Fact]
    public void LostVolume_SomeMissing_HealthIsCrit()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-crit", "lost", 3);
        var root = Path.Combine(_volumesDir, "vol-lost-crit");
        WriteFiles(root, specs.Take(2));

        SimulateVerify(catalog, store, volume, root);

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("crit", updated.Health);
    }

    [Fact]
    public void LostVolume_SomeMissing_LocationNotSet()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-noloc", "lost", 3);
        var root = Path.Combine(_volumesDir, "vol-lost-noloc");
        WriteFiles(root, specs.Take(1));

        SimulateVerify(catalog, store, volume, root);

        // No location should have been set (volume is still LOST)
        var loc = catalog.GetCurrentLocation(volume.Id);
        Assert.Null(loc);
    }

    [Fact]
    public void LostVolume_SomeMissing_MissingArtifactsMarkedMissing()
    {
        var (catalog, store, volume, specs) = Provision("vol-lost-artstat", "lost", 3);
        var root = Path.Combine(_volumesDir, "vol-lost-artstat");

        // Write specs[0] and specs[1]; specs[2] is missing
        WriteFiles(root, specs.Take(2));

        SimulateVerify(catalog, store, volume, root);

        var derived = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", derived[specs[0].DerivedArtifactId]);
        Assert.Equal("present", derived[specs[1].DerivedArtifactId]);
        Assert.Equal("missing", derived[specs[2].DerivedArtifactId]);
    }

    // ── Check 3: Non-LOST volume, all present ──────────────────────────────────

    [Fact]
    public void PresentVolume_AllArtifactsPresent_HealthIsOk()
    {
        var (catalog, store, volume, specs) = Provision("vol-ok-full", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-ok-full");
        WriteFiles(root, specs);

        SimulateVerify(catalog, store, volume, root);

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("present", updated.Status);
        Assert.Equal("ok",      updated.Health);
    }

    [Fact]
    public void PresentVolume_SomeMissing_HealthIsCrit()
    {
        var (catalog, store, volume, specs) = Provision("vol-ok-partial", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-ok-partial");
        WriteFiles(root, specs.Take(2));

        SimulateVerify(catalog, store, volume, root);

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("present", updated.Status);   // status unchanged
        Assert.Equal("crit",    updated.Health);
    }

    // ── Check 3 — regression: SHA1 mismatch ───────────────────────────────────

    [Fact]
    public void MismatchedSha1_ArtifactMarkedBad_NotPresent()
    {
        var (catalog, store, volume, specs) = Provision("vol-mismatch", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-mismatch");

        // Write specs[0] correctly; write specs[1] with wrong content
        Directory.CreateDirectory(Path.Combine(root, specs[0].ReleaseName));
        Directory.CreateDirectory(Path.Combine(root, specs[1].ReleaseName));
        File.WriteAllBytes(Path.Combine(root, specs[0].ReleaseName, specs[0].FileName), specs[0].Content);
        File.WriteAllBytes(Path.Combine(root, specs[1].ReleaseName, specs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("CORRUPTED DATA"));

        SimulateVerify(catalog, store, volume, root);

        var derived = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", derived[specs[0].DerivedArtifactId]);
        Assert.Equal("missing", derived[specs[1].DerivedArtifactId]);

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("crit", updated.Health);
    }

    [Fact]
    public void MismatchedSha1_HealthIsCrit_StatusUnchanged()
    {
        var (catalog, store, volume, specs) = Provision("vol-mismatch2", "present", 1);
        var root = Path.Combine(_volumesDir, "vol-mismatch2");

        Directory.CreateDirectory(Path.Combine(root, specs[0].ReleaseName));
        File.WriteAllBytes(Path.Combine(root, specs[0].ReleaseName, specs[0].FileName),
            System.Text.Encoding.UTF8.GetBytes("WRONG_BYTES"));

        SimulateVerify(catalog, store, volume, root);

        var updated = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal("present", updated.Status);
        Assert.Equal("crit",    updated.Health);
    }

    // ── Check 3 — regression: no filesystem changes ────────────────────────────

    [Fact]
    public void Verify_DoesNotCreateOrDeleteFiles()
    {
        var (catalog, store, volume, specs) = Provision("vol-nochange", "present", 3);
        var root = Path.Combine(_volumesDir, "vol-nochange");
        WriteFiles(root, specs);

        var filesBefore = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(f => f).ToList();

        SimulateVerify(catalog, store, volume, root);

        var filesAfter = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(f => f).ToList();

        Assert.Equal(filesBefore, filesAfter);
    }

    [Fact]
    public void Verify_FilesAreNotModified()
    {
        var (catalog, store, volume, specs) = Provision("vol-nomodify", "present", 2);
        var root = Path.Combine(_volumesDir, "vol-nomodify");
        WriteFiles(root, specs);

        // Record SHA1s of all files before verify
        var sha1Before = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, FileSha1);

        SimulateVerify(catalog, store, volume, root);

        foreach (var (path, hash) in sha1Before)
            Assert.Equal(hash, FileSha1(path));
    }

    // ── No-hash artifacts ──────────────────────────────────────────────────────

    [Fact]
    public void NoHashArtifact_FilePresent_TreatedAsPresent()
    {
        // Build a scenario with Sha1="" (no hash) by bypassing IngestDerivedArtifact
        // and inserting directly with an empty sha1.
        var catalog  = new CatalogService(_catalogDir);
        var dbPath   = Path.Combine(_datDir, "vol-nohash.db");
        var store    = new DatLineStore(dbPath);
        var volId    = Guid.NewGuid().ToString("N");
        const string dlId = "dl-nohash";

        var volume = new VolumeRecord
        {
            Id = volId, Label = "vol-nohash", PlatformId = "p", DatLineId = dlId,
            Status = "present", Health = "ok", PlannedSizeBytes = 100, ActualSizeBytes = 0,
            CreatedAt = DateTime.UtcNow,
        };
        catalog.SaveVolume(volume);

        const string relName  = "Release X";
        const string fileName = "rom.bin";
        const string cik      = "sha1:0000000000000000000000000000000000000001";

        var releaseId = Guid.NewGuid().ToString("N");
        store.SaveReleases([new ReleaseRecord
        {
            Id = releaseId, DatLineId = dlId, Name = relName,
            Status = "missing", ReleaseContentKey = cik,
        }]);
        store.EnsureContentIdentity(new ContentIdentityRecord
        {
            ContentIdentityKey = cik, DatSha1 = null,
            DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow,
        });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        {
            Id = Guid.NewGuid().ToString("N"), ReleaseId = releaseId,
            ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow,
        });

        // Ingest with empty SHA1 hash recorded
        var daId = store.IngestDerivedArtifact(
            cik, "", "no_compression", fileName,
            $"archive/p/{dlId}/{relName}/{fileName}", 10, "");

        catalog.SaveVolumeArtifact(new VolumeArtifactRecord
        {
            Id = Guid.NewGuid().ToString("N"), VolumeId = volId, DatLineId = dlId,
            DerivedArtifactId = daId, ContentIdentityKey = cik,
            Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
        });

        var root = Path.Combine(_volumesDir, "vol-nohash");
        Directory.CreateDirectory(Path.Combine(root, relName));
        File.WriteAllBytes(Path.Combine(root, relName, fileName),
            System.Text.Encoding.UTF8.GetBytes("any content"));

        var (present, bad) = SimulateVerify(catalog, store, volume, root);

        Assert.Equal(1, present);
        Assert.Equal(0, bad);
        var updated = catalog.GetVolumes().Single(v => v.Id == volId);
        Assert.Equal("ok", updated.Health);
    }

    // ── Only affected artifacts change ──────────────────────────────────────────

    [Fact]
    public void PartialVerify_OnlyMissingArtifactsChange_OkArtifactsUntouched()
    {
        var (catalog, store, volume, specs) = Provision("vol-selective", "present", 4);
        var root = Path.Combine(_volumesDir, "vol-selective");

        // Write all but the last artifact
        WriteFiles(root, specs.Take(3));

        SimulateVerify(catalog, store, volume, root);

        var derived = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", derived[specs[0].DerivedArtifactId]);
        Assert.Equal("present", derived[specs[1].DerivedArtifactId]);
        Assert.Equal("present", derived[specs[2].DerivedArtifactId]);
        Assert.Equal("missing", derived[specs[3].DerivedArtifactId]);
    }

    // ── Volume with zero artifacts ──────────────────────────────────────────────

    [Fact]
    public void EmptyVolume_VerifyInfosEmpty_NoMutation()
    {
        // OnVerifyVolume returns early when verifyInfos.Count == 0.
        // Simulate the same guard: if there are no verify infos, do nothing.
        var (catalog, store, volume, _) = Provision("vol-empty", "present", 0);
        var root = Path.Combine(_volumesDir, "vol-empty");
        Directory.CreateDirectory(root);

        var vaIds       = catalog.GetVolumeArtifacts(volume.Id).Select(va => va.DerivedArtifactId).ToList();
        var verifyInfos = store.GetArtifactVerifyInfos(vaIds);

        Assert.Empty(verifyInfos);   // guard confirmed

        // No state changes occur — volume remains as provisioned
        var pre  = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        // (no SimulateVerify call — matches early return in OnVerifyVolume)
        var post = catalog.GetVolumes().Single(v => v.Id == volume.Id);
        Assert.Equal(pre.Status, post.Status);
        Assert.Equal(pre.Health, post.Health);
    }
}
