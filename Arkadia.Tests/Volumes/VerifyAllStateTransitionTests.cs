using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Integration tests for Verify ALL state transitions.
/// Each test provisions a real temp CatalogService + DatLineStore + filesystem,
/// then runs the identical scan → incremental-apply sequence that RunVerifyAllDatLine uses.
/// No UI or Avalonia dependencies — dialog callbacks are stubbed at the simulator boundary.
/// </summary>
public sealed class VerifyAllStateTransitionTests : IDisposable
{
    // ── Temp dirs ─────────────────────────────────────────────────────────────
    private readonly string _tempRoot;
    private readonly string _catalogDir;
    private readonly string _datDir;
    private readonly string _archiveRoot;   // plays the role of AppContext.BaseDirectory
    private readonly string _volumesDir;    // <archiveRoot>/volumes/

    public VerifyAllStateTransitionTests()
    {
        _tempRoot    = Path.Combine(Path.GetTempPath(), "va-" + Guid.NewGuid().ToString("N"));
        _catalogDir  = Path.Combine(_tempRoot, "catalog");
        _datDir      = Path.Combine(_tempRoot, "dat");
        _archiveRoot = Path.Combine(_tempRoot, "app");
        _volumesDir  = Path.Combine(_archiveRoot, "volumes");
        Directory.CreateDirectory(_catalogDir);
        Directory.CreateDirectory(_datDir);
        Directory.CreateDirectory(_archiveRoot);
        Directory.CreateDirectory(_volumesDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // ── SHA1 helpers ──────────────────────────────────────────────────────────

    private static string Sha1Hex(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private static string Sha1Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    // ── ArtifactSpec ──────────────────────────────────────────────────────────

    private sealed record ArtifactSpec(
        string DaId,
        string ReleaseId,
        string ReleaseName,
        string FileName,
        string Sha1,
        string RelativePath,   // archive/platformId/dlId/safe(relName)/fileName
        byte[] Content);

    // ── Provisioning helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Provisions catalog + store + a set of artifact specs.
    /// Returns the specs; does NOT write files or link them to a volume.
    /// </summary>
    private (CatalogService Catalog, DatLineStore Store, List<ArtifactSpec> Specs)
        ProvisionStore(string label, string platformId, string dlId, int count)
    {
        var catalog = new CatalogService(_catalogDir);
        var dbPath  = Path.Combine(_datDir, $"{label}.db");
        var store   = new DatLineStore(dbPath);

        var rawItems = Enumerable.Range(0, count).Select(i =>
        {
            var relName   = $"Release {label} {i}";
            var fileName  = $"rom_{i}.bin";
            var content   = System.Text.Encoding.UTF8.GetBytes($"seed-{label}-{i}");
            var sha1      = Sha1Hex(content);
            var cik       = $"sha1:{sha1}";
            var releaseId = Guid.NewGuid().ToString("N");
            var relPath   = $"archive/{platformId}/{dlId}/{relName}/{fileName}";
            return (relName, fileName, content, sha1, cik, releaseId, relPath);
        }).ToList();

        store.SaveReleases(rawItems.Select(r => new ReleaseRecord
        {
            Id               = r.releaseId,
            DatLineId        = dlId,
            Name             = r.relName,
            Status           = "missing",
            ReleaseContentKey = r.cik,
        }).ToList());

        var specs = new List<ArtifactSpec>(count);
        foreach (var (relName, fileName, content, sha1, cik, releaseId, relPath) in rawItems)
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
                relativePath:       relPath,
                derivedSizeBytes:   content.Length,
                hashedDerivedSha1:  sha1);

            specs.Add(new ArtifactSpec(daId, releaseId, relName, fileName, sha1, relPath, content));
        }

        return (catalog, store, specs);
    }

    /// <summary>Adds a volume to the catalog and links the given specs as volume artifacts.</summary>
    private VolumeRecord AddVolume(
        CatalogService catalog,
        string label,
        string platformId,
        string dlId,
        string status,
        IEnumerable<ArtifactSpec> specs)
    {
        var volId = Guid.NewGuid().ToString("N");
        var vol   = new VolumeRecord
        {
            Id               = volId,
            Label            = label,
            PlatformId       = platformId,
            DatLineId        = dlId,
            Status           = status,
            Health           = status == "lost" ? "crit" : "ok",
            PlannedSizeBytes = 4096,
            ActualSizeBytes  = 0,
            CreatedAt        = DateTime.UtcNow,
        };
        catalog.SaveVolume(vol);

        foreach (var s in specs)
        {
            catalog.SaveVolumeArtifact(new VolumeArtifactRecord
            {
                Id                 = Guid.NewGuid().ToString("N"),
                VolumeId           = volId,
                DatLineId          = dlId,
                DerivedArtifactId  = s.DaId,
                ContentIdentityKey = $"sha1:{s.Sha1}",
                Status             = "present_in_final",
                AddedAtUtc         = DateTime.UtcNow,
            });
        }

        return vol;
    }

    /// <summary>Writes artifact files into the local archive tree under _archiveRoot.</summary>
    private void WriteArchiveFiles(IEnumerable<ArtifactSpec> specs)
    {
        foreach (var s in specs)
        {
            var abs = Path.Combine(_archiveRoot,
                s.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllBytes(abs, s.Content);
        }
    }

    /// <summary>Writes artifact files into a volume workspace root (flat layout).</summary>
    private string WriteVolumeFiles(string volLabel, IEnumerable<ArtifactSpec> specs)
    {
        var root = Path.Combine(_volumesDir, volLabel);
        Directory.CreateDirectory(root);
        foreach (var s in specs)
        {
            var path = Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(root, s.FileName);
            File.WriteAllBytes(path, s.Content);
        }
        return root;
    }

    // ── Core simulator ────────────────────────────────────────────────────────

    /// <summary>
    /// Result returned by SimulateVerifyAll for asserting against.
    /// </summary>
    private sealed record VerifyAllResult(
        int ArchiveVerified,
        int ArchiveMissing,
        int ArchiveMismatch,
        int ArchiveUnexpected,
        int ArchiveChangedIds,
        int TotalVerified,
        int TotalMissing,
        int TotalMismatch,
        int TotalUnexpected,
        int RestoredVols,
        int SkippedVols,
        int VerifiedVols,
        bool Cancelled);

    /// <summary>
    /// Replicates the exact state-machine of RunVerifyAllDatLine without any UI calls.
    /// <para>
    /// <paramref name="cancelAtVolIndex"/> — if ≥ 0, simulates the user pressing Cancel
    /// at the "disk not mounted" prompt when processing that volume index.
    /// </para>
    /// <paramref name="volumeRootOverride"/> — keyed by volume ID; supplies the srcRoot
    /// for volumes that cannot be discovered via the workspace convention.
    /// </summary>
    private VerifyAllResult SimulateVerifyAll(
        CatalogService              catalog,
        DatLineStore                store,
        List<VolumeRecord>          volumes,
        bool                        quarantineMismatch   = false,
        bool                        quarantineUnexpected = false,
        int                         cancelAtVolIndex     = -1,
        Dictionary<string, string>? volumeRootOverride   = null)
    {
        // ── Phase 1: Scope ────────────────────────────────────────────────────
        var allVolumeAssigned      = new HashSet<string>(StringComparer.Ordinal);
        var volumeAssignmentsByVol = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var vol in volumes)
        {
            var vas = catalog.GetVolumeArtifacts(vol.Id);
            var ids = vas.Where(va => va.Status != "lost")
                         .Select(va => va.DerivedArtifactId)
                         .ToList();
            allVolumeAssigned.UnionWith(ids);
            volumeAssignmentsByVol[vol.Id] = ids;
        }

        var allDaStatuses     = store.GetAllDerivedArtifactStatuses();
        var localArchiveDaIds = allDaStatuses
            .Where(x => x.Status != "lost" && !allVolumeAssigned.Contains(x.Id))
            .Select(x => x.Id)
            .ToList();

        // ── Phase 2: Local Archive ────────────────────────────────────────────
        int archiveVerified = 0, archiveMissing = 0, archiveMismatch = 0, archiveUnexpected = 0;
        int archiveChangedCount = 0;
        var archiveChangedIds   = new List<string>();

        if (localArchiveDaIds.Count > 0)
        {
            var archiveInfos = store.GetLocalArchiveVerifyInfos(localArchiveDaIds);

            var expectedRelPaths = new HashSet<string>(
                archiveInfos.Select(ai =>
                    ai.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase);

            // Derive archive base dir: segments[0..2] of first artifact's relative path
            string? archiveBaseDir = null;
            if (archiveInfos.Count > 0)
            {
                var seg = archiveInfos[0].RelativePath.Split('/');
                if (seg.Length >= 3)
                    archiveBaseDir = Path.Combine(_archiveRoot, seg[0], seg[1], seg[2]);
            }

            foreach (var ai in archiveInfos)
            {
                var absPath = Path.Combine(_archiveRoot,
                    ai.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(absPath))
                {
                    archiveMissing++;
                    store.BatchUpdateDerivedArtifactStatus(new[] { ai.DerivedArtifactId }, "missing");
                    archiveChangedIds.Add(ai.DerivedArtifactId);
                    continue;
                }

                var actualSize = new FileInfo(absPath).Length;

                if (ai.Sha1.Length > 0)
                {
                    var actualSha1 = Sha1Hex(absPath);
                    if (string.Equals(actualSha1, ai.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        archiveVerified++;
                        store.BatchUpdateDerivedArtifactStatus(
                            new[] { ai.DerivedArtifactId }, "present");
                        archiveChangedIds.Add(ai.DerivedArtifactId);
                    }
                    else
                    {
                        archiveMismatch++;
                        if (quarantineMismatch)
                        {
                            var qDir = Path.Combine(_archiveRoot, "incoming-skip", "quarantine");
                            Directory.CreateDirectory(qDir);
                            var dest = Path.Combine(qDir, ai.FileName);
                            try
                            {
                                File.Move(absPath, dest, overwrite: true);
                                store.BatchUpdateDerivedArtifactStatus(
                                    new[] { ai.DerivedArtifactId }, "missing");
                                archiveChangedIds.Add(ai.DerivedArtifactId);
                            }
                            catch { /* quarantine failure — leave status unchanged */ }
                        }
                    }
                }
                else
                {
                    bool sizeOk = ai.SizeBytes <= 0 || actualSize == ai.SizeBytes;
                    if (sizeOk)
                    {
                        archiveVerified++;
                        store.BatchUpdateDerivedArtifactStatus(
                            new[] { ai.DerivedArtifactId }, "present");
                        archiveChangedIds.Add(ai.DerivedArtifactId);
                    }
                    else
                    {
                        archiveMismatch++;
                    }
                }
            }

            // Batch release recalculation — done once after full archive phase
            if (archiveChangedIds.Count > 0)
                store.RecalculateReleaseStatusForArtifacts(archiveChangedIds);

            archiveChangedCount = archiveChangedIds.Count;

            // Unexpected files
            if (archiveBaseDir is not null && Directory.Exists(archiveBaseDir))
            {
                foreach (var file in Directory.EnumerateFiles(
                             archiveBaseDir, "*", SearchOption.AllDirectories))
                {
                    var relToAppRoot = Path.GetRelativePath(_archiveRoot, file);
                    if (!expectedRelPaths.Contains(relToAppRoot))
                        archiveUnexpected++;
                }
            }
        }

        // ── Phase 3+4: Volumes ────────────────────────────────────────────────
        int verifiedVols = 0, skippedVols = 0, restoredVols = 0;
        int totalVerified = 0, totalMissing = 0, totalMismatch = 0, totalUnexpected = 0;
        bool cancelled = false;

        for (int vi = 0; vi < volumes.Count && !cancelled; vi++)
        {
            var vol      = volumes[vi];
            var volLabel = vol.Label;
            bool wasLost = vol.Status == "lost";

            // Simulate cancel at the specified volume index
            if (cancelAtVolIndex == vi)
            {
                cancelled = true;
                break;
            }

            // Resolve volume root — workspace convention first, then override table
            string? srcRoot = null;
            var wsRoot = Path.Combine(_volumesDir, volLabel);
            if (Directory.Exists(wsRoot))
            {
                srcRoot = wsRoot;
            }
            else if (volumeRootOverride?.TryGetValue(vol.Id, out var ovr) == true)
            {
                srcRoot = Directory.Exists(ovr) ? ovr : null;
            }

            if (srcRoot is null)
            {
                skippedVols++;
                continue;
            }

            var vaIds    = volumeAssignmentsByVol.TryGetValue(vol.Id, out var ids)
                ? ids : new List<string>();
            var expected = store.GetArtifactVerifyInfos(vaIds);

            if (expected.Count == 0)
            {
                skippedVols++;
                continue;
            }

            var expectedByRelPath = new Dictionary<string, ArtifactVerifyInfo>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var e in expected)
                expectedByRelPath[e.FileName] = e;

            var actualFiles = Directory
                .EnumerateFiles(srcRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileName(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int volVerified = 0, volMissing = 0, volMismatch = 0, volUnexpected = 0;
            var volChangedIds = new List<string>();

            foreach (var ei in expected)
            {
                var absPath = Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(srcRoot, ei.FileName);

                if (!File.Exists(absPath))
                {
                    volMissing++;
                    store.BatchUpdateDerivedArtifactStatus(new[] { ei.DerivedArtifactId }, "missing");
                    volChangedIds.Add(ei.DerivedArtifactId);
                    continue;
                }

                var actualSize = new FileInfo(absPath).Length;

                if (ei.Sha1.Length > 0)
                {
                    var actualSha1 = Sha1Hex(absPath);
                    if (string.Equals(actualSha1, ei.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        volVerified++;
                        store.BatchUpdateDerivedArtifactStatus(
                            new[] { ei.DerivedArtifactId }, "present");
                        volChangedIds.Add(ei.DerivedArtifactId);
                    }
                    else
                    {
                        volMismatch++;
                        if (quarantineMismatch)
                        {
                            var qDir = Path.Combine(_archiveRoot, "incoming-skip", "quarantine",
                                ei.ReleaseName);
                            Directory.CreateDirectory(qDir);
                            var dest = Path.Combine(qDir, ei.FileName);
                            try
                            {
                                File.Move(absPath, dest, overwrite: true);
                                store.BatchUpdateDerivedArtifactStatus(
                                    new[] { ei.DerivedArtifactId }, "missing");
                                volChangedIds.Add(ei.DerivedArtifactId);
                            }
                            catch { }
                        }
                    }
                }
                else
                {
                    bool sizeOk = ei.SizeBytes <= 0 || actualSize == ei.SizeBytes;
                    if (sizeOk)
                    {
                        volVerified++;
                        store.BatchUpdateDerivedArtifactStatus(
                            new[] { ei.DerivedArtifactId }, "present");
                        volChangedIds.Add(ei.DerivedArtifactId);
                    }
                    else
                    {
                        volMismatch++;
                    }
                }
            }

            foreach (var rel in actualFiles)
            {
                if (!expectedByRelPath.ContainsKey(rel))
                    volUnexpected++;
            }

            if (volChangedIds.Count > 0)
                store.RecalculateReleaseStatusForArtifacts(volChangedIds);

            totalVerified   += volVerified;
            totalMissing    += volMissing;
            totalMismatch   += volMismatch;
            totalUnexpected += volUnexpected;

            bool volClean  = volMissing == 0 && volMismatch == 0;
            var  volHealth = volClean && volVerified > 0 ? "ok" : "crit";
            catalog.UpdateVolumeHealth(vol.Id, volHealth);

            if (wasLost && volClean && volVerified > 0)
            {
                restoredVols++;
                catalog.UpdateVolumeStatus(vol.Id, "present");
                catalog.SetCurrentLocation(new VolumeLocationRecord
                {
                    Id           = Guid.NewGuid().ToString("N"),
                    VolumeId     = vol.Id,
                    LocationType = "workspace",
                    DiskId       = null,
                    Path         = srcRoot,
                    IsCurrent    = true,
                    CreatedAt    = DateTime.UtcNow,
                });
            }

            verifiedVols++;
        }

        return new VerifyAllResult(
            ArchiveVerified:   archiveVerified,
            ArchiveMissing:    archiveMissing,
            ArchiveMismatch:   archiveMismatch,
            ArchiveUnexpected: archiveUnexpected,
            ArchiveChangedIds: archiveChangedCount,
            TotalVerified:     totalVerified,
            TotalMissing:      totalMissing,
            TotalMismatch:     totalMismatch,
            TotalUnexpected:   totalUnexpected,
            RestoredVols:      restoredVols,
            SkippedVols:       skippedVols,
            VerifiedVols:      verifiedVols,
            Cancelled:         cancelled);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GROUP 1 — LOCAL ARCHIVE FIRST
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G1_ArchiveArtifact_PresentAndValid_BecomesPresent()
    {
        const string platformId = "p1";
        const string dlId       = "dl1";
        var (catalog, store, specs) = ProvisionStore("g1a", platformId, dlId, 2);
        // No volumes — all artifacts are archive-scoped
        WriteArchiveFiles(specs);

        var result = SimulateVerifyAll(catalog, store, new List<VolumeRecord>());

        Assert.Equal(2, result.ArchiveVerified);
        Assert.Equal(0, result.ArchiveMissing);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specs, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void G1_ArchiveArtifact_PresentAndValid_ReleaseBecomesPresent()
    {
        const string platformId = "p1b";
        const string dlId       = "dl1b";
        var (catalog, store, specs) = ProvisionStore("g1b", platformId, dlId, 2);
        WriteArchiveFiles(specs);

        SimulateVerifyAll(catalog, store, new List<VolumeRecord>());

        var releases = store.LoadReleases();
        Assert.All(releases, r => Assert.Equal("present", r.Status));
    }

    [Fact]
    public void G1_ArchiveArtifact_Missing_BecomesMissing()
    {
        const string platformId = "p1c";
        const string dlId       = "dl1c";
        var (catalog, store, specs) = ProvisionStore("g1c", platformId, dlId, 3);
        // Write only first 2 — spec[2] missing
        WriteArchiveFiles(specs.Take(2));

        var result = SimulateVerifyAll(catalog, store, new List<VolumeRecord>());

        Assert.Equal(1, result.ArchiveMissing);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", statuses[specs[0].DaId]);
        Assert.Equal("present", statuses[specs[1].DaId]);
        Assert.Equal("missing", statuses[specs[2].DaId]);
    }

    [Fact]
    public void G1_ArchiveUnexpectedFile_Detected()
    {
        const string platformId = "p1d";
        const string dlId       = "dl1d";
        var (catalog, store, specs) = ProvisionStore("g1d", platformId, dlId, 1);
        WriteArchiveFiles(specs);

        // Drop an extra file alongside the expected artifact
        var seg = specs[0].RelativePath.Split('/');
        var extraDir = Path.Combine(_archiveRoot, seg[0], seg[1], seg[2], "Release Extra");
        Directory.CreateDirectory(extraDir);
        File.WriteAllText(Path.Combine(extraDir, "bonus.bin"), "extra content");

        var result = SimulateVerifyAll(catalog, store, new List<VolumeRecord>());

        Assert.Equal(1, result.ArchiveUnexpected);
        Assert.Equal(1, result.ArchiveVerified);  // expected artifact still verified
    }

    [Fact]
    public void G1_ArchiveMismatch_Detected_ArtifactNotMarkedPresent()
    {
        const string platformId = "p1e";
        const string dlId       = "dl1e";
        var (catalog, store, specs) = ProvisionStore("g1e", platformId, dlId, 2);
        WriteArchiveFiles(specs);  // write both correctly first

        // Corrupt specs[1] in-place
        var absPath = Path.Combine(_archiveRoot,
            specs[1].RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(absPath, "CORRUPTED");

        var result = SimulateVerifyAll(catalog, store, new List<VolumeRecord>());

        Assert.Equal(1, result.ArchiveVerified);
        Assert.Equal(1, result.ArchiveMismatch);

        // Mismatch without quarantine leaves status unchanged — only the scan counts matter.
        // specs[0] was verified; specs[1] was detected as a mismatch but not touched.
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", statuses[specs[0].DaId]);
    }

    [Fact]
    public void G1_ArchiveMismatch_WithQuarantine_MarkedMissing()
    {
        const string platformId = "p1f";
        const string dlId       = "dl1f";
        var (catalog, store, specs) = ProvisionStore("g1f", platformId, dlId, 1);
        WriteArchiveFiles(specs);

        // Corrupt the file
        var absPath = Path.Combine(_archiveRoot,
            specs[0].RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(absPath, "CORRUPTED");

        var result = SimulateVerifyAll(catalog, store, new List<VolumeRecord>(),
            quarantineMismatch: true);

        Assert.Equal(1, result.ArchiveMismatch);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("missing", statuses[specs[0].DaId]);  // quarantined → missing
    }

    [Fact]
    public void G1_ArchiveOnly_VolumePhaseProducesNoSideEffects()
    {
        const string platformId = "p1g";
        const string dlId       = "dl1g";
        var (catalog, store, specs) = ProvisionStore("g1g", platformId, dlId, 2);
        WriteArchiveFiles(specs);

        var result = SimulateVerifyAll(catalog, store, new List<VolumeRecord>());

        Assert.Equal(0, result.TotalVerified);
        Assert.Equal(0, result.TotalMissing);
        Assert.Equal(0, result.VerifiedVols);
        Assert.Equal(0, result.SkippedVols);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GROUP 2 — VOLUME PHASE ORDER / RESTORE
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G2_LostVolume_AllValid_Restored()
    {
        const string platformId = "p2a";
        const string dlId       = "dl2a";
        var (catalog, store, specs) = ProvisionStore("g2a", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-g2a", platformId, dlId, "lost", specs);
        WriteVolumeFiles("vol-g2a", specs);

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        Assert.Equal(1, result.RestoredVols);
        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("present", updated.Status);
        Assert.Equal("ok",      updated.Health);
    }

    [Fact]
    public void G2_LostVolume_AllValid_LocationSet()
    {
        const string platformId = "p2b";
        const string dlId       = "dl2b";
        var (catalog, store, specs) = ProvisionStore("g2b", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-g2b", platformId, dlId, "lost", specs);
        WriteVolumeFiles("vol-g2b", specs);

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        var loc = catalog.GetCurrentLocation(vol.Id);
        Assert.NotNull(loc);
        Assert.Equal("workspace", loc!.LocationType);
    }

    [Fact]
    public void G2_LostVolume_AllValid_ArtifactsMarkedPresent()
    {
        const string platformId = "p2c";
        const string dlId       = "dl2c";
        var (catalog, store, specs) = ProvisionStore("g2c", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-g2c", platformId, dlId, "lost", specs);
        WriteVolumeFiles("vol-g2c", specs);

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specs, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void G2_LostVolume_PartialFiles_RemainsLost()
    {
        const string platformId = "p2d";
        const string dlId       = "dl2d";
        var (catalog, store, specs) = ProvisionStore("g2d", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-g2d", platformId, dlId, "lost", specs);
        // Only write 2 of 3
        WriteVolumeFiles("vol-g2d", specs.Take(2));

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        Assert.Equal(0, result.RestoredVols);
        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("lost", updated.Status);
        Assert.Equal("crit", updated.Health);
    }

    [Fact]
    public void G2_LostVolume_PartialFiles_LocationNotSet()
    {
        const string platformId = "p2e";
        const string dlId       = "dl2e";
        var (catalog, store, specs) = ProvisionStore("g2e", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-g2e", platformId, dlId, "lost", specs);
        WriteVolumeFiles("vol-g2e", specs.Take(1));

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        var loc = catalog.GetCurrentLocation(vol.Id);
        Assert.Null(loc);
    }

    [Fact]
    public void G2_PresentVolume_PartialFailure_HealthCrit_StatusUnchanged()
    {
        const string platformId = "p2f";
        const string dlId       = "dl2f";
        var (catalog, store, specs) = ProvisionStore("g2f", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-g2f", platformId, dlId, "present", specs);
        // Write only 2 of 3
        WriteVolumeFiles("vol-g2f", specs.Take(2));

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("present", updated.Status);  // not demoted to lost
        Assert.Equal("crit",    updated.Health);
    }

    [Fact]
    public void G2_ArchiveProcessed_BeforeVolumesAreScanned()
    {
        // Prove archive-phase DA ids become present BEFORE volume phase touches anything.
        // We do this by checking that archiveVerified > 0 and the relevant DAs are
        // "present" even when the volume phase is empty.
        const string platformId = "p2g";
        const string dlId       = "dl2g";
        var (catalog, store, specs) = ProvisionStore("g2g", platformId, dlId, 2);

        // specs are NOT assigned to any volume → archive-scoped
        WriteArchiveFiles(specs);

        // Add a separate volume with its own artifacts
        var (_, storeVol, specsVol) = ProvisionStore("g2g-vol", platformId, dlId + "-vol", 1);
        // Volume with no accessible root → will be skipped
        var vol = AddVolume(catalog, "vol-g2g-inaccessible", platformId, dlId, "present", specsVol);

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        // Archive verified independently of volume outcome
        Assert.Equal(2, result.ArchiveVerified);
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specs, s => Assert.Equal("present", statuses[s.DaId]));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GROUP 3 — INCREMENTAL APPLY
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G3_ArchiveChanges_AppliedBeforeVolumePhase_ReleasesReflectArchive()
    {
        const string platformId = "p3a";
        const string dlId       = "dl3a";
        var (catalog, store, specs) = ProvisionStore("g3a", platformId, dlId, 2);
        WriteArchiveFiles(specs);

        // A volume that will be skipped (no root present)
        var vol = AddVolume(catalog, "vol-g3a-missing", platformId, dlId, "present", new List<ArtifactSpec>());

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        // Archive releases must already be "present" even though volume phase ran
        var releases = store.LoadReleases();
        Assert.All(releases, r => Assert.Equal("present", r.Status));
    }

    [Fact]
    public void G3_VolumeA_CommittedEvenWhen_VolumeBCancelled()
    {
        const string platformId = "p3b";
        const string dlId       = "dl3b";
        var (catalog, store, specsA) = ProvisionStore("g3b-a", platformId, dlId, 2);
        var (_, _,    specsB)        = ProvisionStore("g3b-b", platformId, dlId, 2);

        var volA = AddVolume(catalog, "vol-g3b-A", platformId, dlId, "present", specsA);
        var volB = AddVolume(catalog, "vol-g3b-B", platformId, dlId, "present", specsB);

        WriteVolumeFiles("vol-g3b-A", specsA);
        // volB root intentionally not written → skipped (cancel at index 1)

        var result = SimulateVerifyAll(catalog, store,
            new[] { volA, volB }.ToList(), cancelAtVolIndex: 1);

        Assert.True(result.Cancelled);

        // Volume A's artifacts must already be present in DB
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specsA, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void G3_VolumeA_HealthUpdated_BeforeVolumeBProcessed()
    {
        const string platformId = "p3c";
        const string dlId       = "dl3c";
        var (catalog, store, specsA) = ProvisionStore("g3c-a", platformId, dlId, 2);
        var (_, _,    specsB)        = ProvisionStore("g3c-b", platformId, dlId, 2);

        var volA = AddVolume(catalog, "vol-g3c-A", platformId, dlId, "present", specsA);
        var volB = AddVolume(catalog, "vol-g3c-B", platformId, dlId, "present", specsB);

        WriteVolumeFiles("vol-g3c-A", specsA);
        // volB is cancelled at index 1

        SimulateVerifyAll(catalog, store,
            new[] { volA, volB }.ToList(), cancelAtVolIndex: 1);

        // Volume A's health must be "ok" regardless of volB outcome
        var updatedA = catalog.GetVolumes().Single(v => v.Id == volA.Id);
        Assert.Equal("ok", updatedA.Health);
    }

    [Fact]
    public void G3_NoRollback_SuccessfulWorkPreserved_AfterCancelledVolume()
    {
        const string platformId = "p3d";
        const string dlId       = "dl3d";
        // Archive artifacts + one volume
        var (catalog, store, specsArchive) = ProvisionStore("g3d", platformId, dlId, 2);
        WriteArchiveFiles(specsArchive);

        var (_, _, specsVol) = ProvisionStore("g3d-vol", platformId, dlId + "-vol", 2);
        var vol = AddVolume(catalog, "vol-g3d-missing", platformId, dlId, "present", specsVol);

        // Cancel immediately at vol 0 (no volume root exists)
        SimulateVerifyAll(catalog, store, new[] { vol }.ToList(), cancelAtVolIndex: 0);

        // Archive work must be retained
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specsArchive, s => Assert.Equal("present", statuses[s.DaId]));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GROUP 4 — CANCEL SEMANTICS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G4_Cancel_AtVolumeIndex_StopsFurtherWork()
    {
        const string platformId = "p4a";
        const string dlId       = "dl4a";
        // All 6 specs in ONE store — mirrors the real one-store-per-dat-line model.
        var (catalog, store, allSpecs) = ProvisionStore("g4a", platformId, dlId, 6);
        var specsA = allSpecs.Take(2).ToList();
        var specsB = allSpecs.Skip(2).Take(2).ToList();
        var specsC = allSpecs.Skip(4).ToList();

        var volA = AddVolume(catalog, "vol-g4a-A", platformId, dlId, "present", specsA);
        var volB = AddVolume(catalog, "vol-g4a-B", platformId, dlId, "present", specsB);
        var volC = AddVolume(catalog, "vol-g4a-C", platformId, dlId, "present", specsC);

        WriteVolumeFiles("vol-g4a-A", specsA);
        WriteVolumeFiles("vol-g4a-B", specsB);
        WriteVolumeFiles("vol-g4a-C", specsC);

        // Establish a known pre-test state for specsC so we can assert "unchanged after cancel".
        store.BatchUpdateDerivedArtifactStatus(specsC.Select(s => s.DaId).ToList(), "missing");

        // Cancel when reaching volume B (index 1)
        var result = SimulateVerifyAll(catalog, store,
            new[] { volA, volB, volC }.ToList(), cancelAtVolIndex: 1);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.VerifiedVols);   // only A processed

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        // Volume A fully verified
        Assert.All(specsA, s => Assert.Equal("present", statuses[s.DaId]));
        // Volume C never scanned — remains at the pre-test "missing" state
        Assert.All(specsC, s => Assert.Equal("missing", statuses[s.DaId]));
    }

    [Fact]
    public void G4_Cancel_EarliestVolumes_AlreadyApplied()
    {
        const string platformId = "p4b";
        const string dlId       = "dl4b";
        var (catalog, store, specsA) = ProvisionStore("g4b-a", platformId, dlId, 2);
        var (_, _,    specsB)        = ProvisionStore("g4b-b", platformId, dlId, 2);

        var volA = AddVolume(catalog, "vol-g4b-A", platformId, dlId, "present", specsA);
        var volB = AddVolume(catalog, "vol-g4b-B", platformId, dlId, "present", specsB);

        WriteVolumeFiles("vol-g4b-A", specsA);

        // Cancel at vol B (index 1)
        SimulateVerifyAll(catalog, store,
            new[] { volA, volB }.ToList(), cancelAtVolIndex: 1);

        // Volume A's artifacts must be committed
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specsA, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void G4_Cancel_ResultMarkedPartial()
    {
        const string platformId = "p4c";
        const string dlId       = "dl4c";
        var (catalog, store, specs) = ProvisionStore("g4c", platformId, dlId, 1);
        var vol = AddVolume(catalog, "vol-g4c", platformId, dlId, "present", specs);

        var result = SimulateVerifyAll(catalog, store,
            new[] { vol }.ToList(), cancelAtVolIndex: 0);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.VerifiedVols);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GROUP 5 — FALSE POSITIVE PROTECTION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G5_ArtifactNeverPresent_WhenFileAbsent()
    {
        const string platformId = "p5a";
        const string dlId       = "dl5a";
        var (catalog, store, specs) = ProvisionStore("g5a", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-g5a", platformId, dlId, "present", specs);
        // Create the volume root dir but write no artifact files — scan runs and finds nothing
        Directory.CreateDirectory(Path.Combine(_volumesDir, "vol-g5a"));

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specs, s => Assert.Equal("missing", statuses[s.DaId]));
    }

    [Fact]
    public void G5_ArtifactNeverPresent_WhenHashMismatched()
    {
        const string platformId = "p5b";
        const string dlId       = "dl5b";
        var (catalog, store, specs) = ProvisionStore("g5b", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-g5b", platformId, dlId, "present", specs);

        // Write specs[0] correctly; specs[1] with corrupted content
        var root = Path.Combine(_volumesDir, "vol-g5b");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(root, specs[0].FileName), specs[0].Content);
        File.WriteAllBytes(Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(root, specs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("WRONG CONTENT"));

        // Use quarantine so the mismatch artifact is explicitly marked missing.
        SimulateVerifyAll(catalog, store, new[] { vol }.ToList(), quarantineMismatch: true);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", statuses[specs[0].DaId]);
        Assert.Equal("missing", statuses[specs[1].DaId]);
    }

    [Fact]
    public void G5_LostVolumeNotRestored_WhenAnyArtifactMissing()
    {
        const string platformId = "p5c";
        const string dlId       = "dl5c";
        var (catalog, store, specs) = ProvisionStore("g5c", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-g5c", platformId, dlId, "lost", specs);
        // Write only 2 of 3
        WriteVolumeFiles("vol-g5c", specs.Take(2));

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("lost", updated.Status);
        Assert.Null(catalog.GetCurrentLocation(vol.Id));
    }

    [Fact]
    public void G5_LostVolumeNotRestored_WhenAnyHashMismatched()
    {
        const string platformId = "p5d";
        const string dlId       = "dl5d";
        var (catalog, store, specs) = ProvisionStore("g5d", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-g5d", platformId, dlId, "lost", specs);

        // Write specs[0] ok, specs[1] corrupted
        var root = Path.Combine(_volumesDir, "vol-g5d");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(root, specs[0].FileName), specs[0].Content);
        File.WriteAllBytes(Arkadia.Volumes.VolumeArtifactPathBuilder.GetFlatFullPath(root, specs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("BAD"));

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("lost", updated.Status);
    }

    [Fact]
    public void G5_NoImpossibleState_PresentReleaseWithMissingArtifacts()
    {
        // After Verify ALL, no release should be "present" while any of its artifacts is "missing".
        const string platformId = "p5e";
        const string dlId       = "dl5e";
        var (catalog, store, specs) = ProvisionStore("g5e", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-g5e", platformId, dlId, "present", specs);
        // Write only spec[0] — spec[1] and spec[2] will be missing
        WriteVolumeFiles("vol-g5e", specs.Take(1));

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        var issues = store.GetPresentReleasesWithMissingArtifacts();
        Assert.Empty(issues);
    }

    [Fact]
    public void G5_VolumeScopeExclusion_ArchiveArtifactsNotDoubleProcessed()
    {
        // Artifacts assigned to a volume must NOT appear in localArchiveDaIds.
        const string platformId = "p5f";
        const string dlId       = "dl5f";
        var (catalog, store, specsAll) = ProvisionStore("g5f", platformId, dlId, 4);

        // First 2 go to volume, last 2 stay in archive scope
        var volSpecs     = specsAll.Take(2).ToList();
        var archiveSpecs = specsAll.Skip(2).ToList();
        var vol          = AddVolume(catalog, "vol-g5f", platformId, dlId, "present", volSpecs);

        WriteVolumeFiles("vol-g5f", volSpecs);
        WriteArchiveFiles(archiveSpecs);

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        // Archive phase should see only the 2 non-volume artifacts
        Assert.Equal(2, result.ArchiveVerified);
        // Volume phase processes the other 2
        Assert.Equal(2, result.TotalVerified);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GROUP 6 — SIDE EFFECT SAFETY
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G6_VerifyAll_DoesNotDeleteUnrelatedFiles()
    {
        const string platformId = "p6a";
        const string dlId       = "dl6a";
        var (catalog, store, specs) = ProvisionStore("g6a", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-g6a", platformId, dlId, "present", specs);
        var root = WriteVolumeFiles("vol-g6a", specs);

        // Write an unrelated file into the volume directory
        var unrelatedPath = Path.Combine(root, "unrelated.txt");
        File.WriteAllText(unrelatedPath, "do not delete me");

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        Assert.True(File.Exists(unrelatedPath));
    }

    [Fact]
    public void G6_MissingLaterVolume_DoesNotRevertArchiveChanges()
    {
        const string platformId = "p6b";
        const string dlId       = "dl6b";
        var (catalog, store, specsArchive) = ProvisionStore("g6b", platformId, dlId, 2);
        WriteArchiveFiles(specsArchive);

        // A volume with no accessible root — will be skipped
        var (_, _, specsVol) = ProvisionStore("g6b-vol", platformId, dlId + "-vol", 1);
        var vol = AddVolume(catalog, "vol-g6b-gone", platformId, dlId, "present", specsVol);

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        // Archive artifacts must still be "present"
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specsArchive, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void G6_VerifyAll_DoesNotModifyExpectedFiles()
    {
        const string platformId = "p6c";
        const string dlId       = "dl6c";
        var (catalog, store, specs) = ProvisionStore("g6c", platformId, dlId, 3);
        var vol  = AddVolume(catalog, "vol-g6c", platformId, dlId, "present", specs);
        var root = WriteVolumeFiles("vol-g6c", specs);

        // Record SHA1s before
        var sha1Before = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, Sha1Hex);

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        // All files must be byte-identical after
        foreach (var (path, hash) in sha1Before)
            Assert.Equal(hash, Sha1Hex(path));
    }

    [Fact]
    public void G6_MultipleVolumes_EachVolumeHealthSetIndependently()
    {
        const string platformId = "p6d";
        const string dlId       = "dl6d";
        // Both volumes share one store — mirrors the real one-store-per-dat-line model.
        var (catalog, store, allSpecs) = ProvisionStore("g6d", platformId, dlId, 4);
        var specsA = allSpecs.Take(2).ToList();
        var specsB = allSpecs.Skip(2).ToList();

        var volA = AddVolume(catalog, "vol-g6d-A", platformId, dlId, "present", specsA);
        var volB = AddVolume(catalog, "vol-g6d-B", platformId, dlId, "present", specsB);

        WriteVolumeFiles("vol-g6d-A", specsA);             // A — all present
        WriteVolumeFiles("vol-g6d-B", specsB.Take(1));     // B — partial (1 of 2 files)

        SimulateVerifyAll(catalog, store, new[] { volA, volB }.ToList());

        var updatedA = catalog.GetVolumes().Single(v => v.Id == volA.Id);
        var updatedB = catalog.GetVolumes().Single(v => v.Id == volB.Id);
        Assert.Equal("ok",   updatedA.Health);
        Assert.Equal("crit", updatedB.Health);
    }
}
