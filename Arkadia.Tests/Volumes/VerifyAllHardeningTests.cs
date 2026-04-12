using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Volumes;

/// <summary>
/// Hardening and chaos tests for Verify ALL under failure, cancel, and partial-progress conditions.
///
/// Each test uses real temp SQLite + temp filesystem. No UI dependencies.
/// The simulator replicates the exact state machine of RunVerifyAllDatLine with injectable
/// failure modes: quarantine failure, disk-not-mounted, cancel-at-index, retry semantics.
///
/// Invariants under test:
///   1. Archive phase always runs before volume phase.
///   2. DB writes are applied per-artifact — no all-or-nothing batching.
///   3. Cancel stops future work; prior committed changes are preserved unconditionally.
///   4. Quarantine failure → mismatch counted + health CRIT, but artifact status unchanged.
///   5. LOST volume: only restored when every required artifact is verified clean.
///   6. Result flags (Cancelled) accurately distinguish ok / partial.
/// </summary>
public sealed class VerifyAllHardeningTests : IDisposable
{
    // ── Temp layout ───────────────────────────────────────────────────────────

    private readonly string _tempRoot;
    private readonly string _catalogDir;
    private readonly string _datDir;
    private readonly string _archiveRoot;  // plays the role of AppContext.BaseDirectory
    private readonly string _volumesDir;   // <archiveRoot>/volumes/

    public VerifyAllHardeningTests()
    {
        _tempRoot    = Path.Combine(Path.GetTempPath(), "vh-" + Guid.NewGuid().ToString("N"));
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Sha1Hex(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private static string Sha1Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    private sealed record ArtifactSpec(
        string DaId,
        string ReleaseName,
        string FileName,
        string Sha1,
        string RelativePath,
        byte[] Content);

    // ── Provisioning ──────────────────────────────────────────────────────────

    private (CatalogService Catalog, DatLineStore Store, List<ArtifactSpec> Specs)
        ProvisionStore(string label, string platformId, string dlId, int count)
    {
        var catalog = new CatalogService(_catalogDir);
        var dbPath  = Path.Combine(_datDir, $"{label}.db");
        var store   = new DatLineStore(dbPath);

        var rawItems = Enumerable.Range(0, count).Select(i =>
        {
            var relName   = $"Rel {label} {i}";
            var fileName  = $"f{i}.bin";
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
                DatSha1 = sha1, DatMd5 = null, DatCrc32 = null,
                CreatedAtUtc = DateTime.UtcNow,
            });
            store.SaveReleaseContentLink(new ReleaseContentLinkRecord
            {
                Id = Guid.NewGuid().ToString("N"), ReleaseId = releaseId,
                ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow,
            });
            var daId = store.IngestDerivedArtifact(cik, "", "no_compression",
                fileName, relPath, content.Length, sha1);
            specs.Add(new ArtifactSpec(daId, relName, fileName, sha1, relPath, content));
        }

        return (catalog, store, specs);
    }

    private VolumeRecord AddVolume(
        CatalogService catalog, string label, string platformId, string dlId,
        string status, IEnumerable<ArtifactSpec> specs)
    {
        var volId = Guid.NewGuid().ToString("N");
        var vol = new VolumeRecord
        {
            Id = volId, Label = label, PlatformId = platformId, DatLineId = dlId,
            Status = status, Health = status == "lost" ? "crit" : "ok",
            PlannedSizeBytes = 4096, ActualSizeBytes = 0, CreatedAt = DateTime.UtcNow,
        };
        catalog.SaveVolume(vol);
        foreach (var s in specs)
        {
            catalog.SaveVolumeArtifact(new VolumeArtifactRecord
            {
                Id = Guid.NewGuid().ToString("N"), VolumeId = volId, DatLineId = dlId,
                DerivedArtifactId = s.DaId, ContentIdentityKey = $"sha1:{s.Sha1}",
                Status = "present_in_final", AddedAtUtc = DateTime.UtcNow,
            });
        }
        return vol;
    }

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

    private string WriteVolumeFiles(string volLabel, IEnumerable<ArtifactSpec> specs)
    {
        var root = Path.Combine(_volumesDir, volLabel);
        foreach (var s in specs)
        {
            var dir = Path.Combine(root, s.ReleaseName);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, s.FileName), s.Content);
        }
        return root;
    }

    // ── Simulator ─────────────────────────────────────────────────────────────

    /// <summary>Extended result type tracking quarantine outcomes separately.</summary>
    private sealed record HardeningResult(
        int  ArchiveVerified,
        int  ArchiveMissing,
        int  ArchiveMismatch,
        int  ArchiveQuarantined,
        int  ArchiveQuarantineFailed,
        int  TotalVerified,
        int  TotalMissing,
        int  TotalMismatch,
        int  TotalQuarantined,
        int  TotalQuarantineFailed,
        int  RestoredVols,
        int  SkippedVols,
        int  VerifiedVols,
        bool Cancelled);

    /// <summary>
    /// Replicates the state machine of RunVerifyAllDatLine with injectable failure modes.
    ///
    /// <paramref name="simulateQuarantineFailure"/>: when true, every quarantine attempt
    ///   returns false (move not performed, error recorded), testing the failure branch.
    ///
    /// <paramref name="firstAttemptFailVolIds"/>: volume IDs whose root resolution returns null
    ///   on the first try (simulates disk-not-mounted). The simulator then retries once:
    ///   if the volume directory actually exists the retry succeeds; otherwise it skips.
    ///
    /// <paramref name="cancelWhenNotMounted"/>: when true and the first-attempt root lookup
    ///   returns null, the simulator cancels immediately (simulates user pressing Cancel at
    ///   the disk-not-mounted prompt) rather than retrying.
    ///
    /// <paramref name="cancelAtVolIndex"/>: if ≥ 0, simulate user-cancel before processing
    ///   that volume index.
    /// </summary>
    private HardeningResult SimulateVerifyAll(
        CatalogService              catalog,
        DatLineStore                store,
        List<VolumeRecord>          volumes,
        bool                        quarantineMismatch           = false,
        bool                        simulateQuarantineFailure    = false,
        int                         cancelAtVolIndex             = -1,
        IReadOnlySet<string>?       firstAttemptFailVolIds       = null,
        bool                        cancelWhenNotMounted         = false,
        Dictionary<string, string>? volumeRootOverride           = null)
    {
        // ── Phase 1: Build scope ──────────────────────────────────────────────
        var allVolumeAssigned      = new HashSet<string>(StringComparer.Ordinal);
        var volumeAssignmentsByVol = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var vol in volumes)
        {
            var vas = catalog.GetVolumeArtifacts(vol.Id);
            var ids = vas.Where(va => va.Status != "lost")
                         .Select(va => va.DerivedArtifactId).ToList();
            allVolumeAssigned.UnionWith(ids);
            volumeAssignmentsByVol[vol.Id] = ids;
        }
        var allDaStatuses     = store.GetAllDerivedArtifactStatuses();
        var localArchiveDaIds = allDaStatuses
            .Where(x => x.Status != "lost" && !allVolumeAssigned.Contains(x.Id))
            .Select(x => x.Id).ToList();

        // ── Phase 2: Local Archive ────────────────────────────────────────────
        int archiveVerified = 0, archiveMissing = 0, archiveMismatch = 0;
        int archiveQuarantined = 0, archiveQuarantineFailed = 0;
        var archiveChangedIds = new List<string>();

        if (localArchiveDaIds.Count > 0)
        {
            var archiveInfos = store.GetLocalArchiveVerifyInfos(localArchiveDaIds);

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
                        store.BatchUpdateDerivedArtifactStatus(new[] { ai.DerivedArtifactId }, "present");
                        archiveChangedIds.Add(ai.DerivedArtifactId);
                    }
                    else
                    {
                        archiveMismatch++;
                        if (quarantineMismatch)
                        {
                            bool moved = TryQuarantineSimulated(absPath, ai.FileName,
                                simulateQuarantineFailure);
                            if (moved)
                            {
                                archiveQuarantined++;
                                store.BatchUpdateDerivedArtifactStatus(
                                    new[] { ai.DerivedArtifactId }, "missing");
                                archiveChangedIds.Add(ai.DerivedArtifactId);
                            }
                            else
                            {
                                archiveQuarantineFailed++;
                                // Status intentionally left unchanged — state unknown
                            }
                        }
                        // Without quarantine: status unchanged (intentional — state unknown)
                    }
                }
                else
                {
                    bool sizeOk = ai.SizeBytes <= 0 || actualSize == ai.SizeBytes;
                    if (sizeOk)
                    {
                        archiveVerified++;
                        store.BatchUpdateDerivedArtifactStatus(new[] { ai.DerivedArtifactId }, "present");
                        archiveChangedIds.Add(ai.DerivedArtifactId);
                    }
                    else
                    {
                        archiveMismatch++;
                    }
                }
            }

            if (archiveChangedIds.Count > 0)
                store.RecalculateReleaseStatusForArtifacts(archiveChangedIds);
        }

        // ── Phase 3+4: Volumes ────────────────────────────────────────────────
        int verifiedVols = 0, skippedVols = 0, restoredVols = 0;
        int totalVerified = 0, totalMissing = 0, totalMismatch = 0;
        int totalQuarantined = 0, totalQuarantineFailed = 0;
        bool cancelled = false;

        for (int vi = 0; vi < volumes.Count && !cancelled; vi++)
        {
            var vol      = volumes[vi];
            bool wasLost = vol.Status == "lost";

            // Explicit cancel at this index (simulates user pressing Cancel at disk prompt)
            if (cancelAtVolIndex == vi)
            {
                cancelled = true;
                break;
            }

            // Resolve root — first attempt
            string? srcRoot = ResolveVolumeRoot(vol, isRetry: false,
                firstAttemptFailVolIds, volumeRootOverride);

            if (srcRoot is null)
            {
                if (cancelWhenNotMounted)
                {
                    // Simulates user pressing Cancel at the "disk not mounted" dialog
                    cancelled = true;
                    break;
                }
                // Simulates user pressing OK (retry) — re-discover
                srcRoot = ResolveVolumeRoot(vol, isRetry: true,
                    firstAttemptFailVolIds, volumeRootOverride);
                if (srcRoot is null)
                {
                    skippedVols++;
                    continue;
                }
            }

            var vaIds    = volumeAssignmentsByVol.TryGetValue(vol.Id, out var ids) ? ids : new List<string>();
            var expected = store.GetArtifactVerifyInfos(vaIds);
            if (expected.Count == 0) { skippedVols++; continue; }

            var expectedByRelPath = new Dictionary<string, ArtifactVerifyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in expected)
                expectedByRelPath[Path.Combine(e.ReleaseName, e.FileName)] = e;

            int volVerified = 0, volMissing = 0, volMismatch = 0;
            int volQuarantined = 0, volQuarantineFailed = 0;
            var volChangedIds = new List<string>();

            foreach (var ei in expected)
            {
                var absPath = Path.Combine(srcRoot, ei.ReleaseName, ei.FileName);

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
                        store.BatchUpdateDerivedArtifactStatus(new[] { ei.DerivedArtifactId }, "present");
                        volChangedIds.Add(ei.DerivedArtifactId);
                    }
                    else
                    {
                        volMismatch++;
                        if (quarantineMismatch)
                        {
                            bool moved = TryQuarantineSimulated(absPath, ei.FileName,
                                simulateQuarantineFailure);
                            if (moved)
                            {
                                volQuarantined++;
                                store.BatchUpdateDerivedArtifactStatus(
                                    new[] { ei.DerivedArtifactId }, "missing");
                                volChangedIds.Add(ei.DerivedArtifactId);
                            }
                            else
                            {
                                volQuarantineFailed++;
                                // Status intentionally unchanged — mirrors production behaviour
                            }
                        }
                    }
                }
                else
                {
                    bool sizeOk = ei.SizeBytes <= 0 || actualSize == ei.SizeBytes;
                    if (sizeOk)
                    {
                        volVerified++;
                        store.BatchUpdateDerivedArtifactStatus(new[] { ei.DerivedArtifactId }, "present");
                        volChangedIds.Add(ei.DerivedArtifactId);
                    }
                    else
                    {
                        volMismatch++;
                    }
                }
            }

            if (volChangedIds.Count > 0)
                store.RecalculateReleaseStatusForArtifacts(volChangedIds);

            totalVerified       += volVerified;
            totalMissing        += volMissing;
            totalMismatch       += volMismatch;
            totalQuarantined    += volQuarantined;
            totalQuarantineFailed += volQuarantineFailed;

            bool volClean  = volMissing == 0 && volMismatch == 0;
            var  volHealth = volClean && volVerified > 0 ? "ok" : "crit";
            catalog.UpdateVolumeHealth(vol.Id, volHealth);

            if (wasLost && volClean && volVerified > 0)
            {
                restoredVols++;
                catalog.UpdateVolumeStatus(vol.Id, "present");
                catalog.SetCurrentLocation(new VolumeLocationRecord
                {
                    Id = Guid.NewGuid().ToString("N"), VolumeId = vol.Id,
                    LocationType = "workspace", DiskId = null,
                    Path = srcRoot, IsCurrent = true, CreatedAt = DateTime.UtcNow,
                });
            }

            verifiedVols++;
        }

        return new HardeningResult(
            ArchiveVerified:       archiveVerified,
            ArchiveMissing:        archiveMissing,
            ArchiveMismatch:       archiveMismatch,
            ArchiveQuarantined:    archiveQuarantined,
            ArchiveQuarantineFailed: archiveQuarantineFailed,
            TotalVerified:         totalVerified,
            TotalMissing:          totalMissing,
            TotalMismatch:         totalMismatch,
            TotalQuarantined:      totalQuarantined,
            TotalQuarantineFailed: totalQuarantineFailed,
            RestoredVols:          restoredVols,
            SkippedVols:           skippedVols,
            VerifiedVols:          verifiedVols,
            Cancelled:             cancelled);
    }

    /// <summary>
    /// Resolves volume root from workspace convention or override table.
    /// Returns null when <paramref name="firstAttemptFailVolIds"/> contains vol.Id
    /// and isRetry is false, simulating disk-not-mounted on first attempt.
    /// </summary>
    private string? ResolveVolumeRoot(
        VolumeRecord vol, bool isRetry,
        IReadOnlySet<string>? firstAttemptFailVolIds,
        Dictionary<string, string>? volumeRootOverride)
    {
        if (!isRetry && firstAttemptFailVolIds?.Contains(vol.Id) == true)
            return null;

        if (volumeRootOverride?.TryGetValue(vol.Id, out var ovr) == true)
            return Directory.Exists(ovr) ? ovr : null;

        var wsRoot = Path.Combine(_volumesDir, vol.Label);
        return Directory.Exists(wsRoot) ? wsRoot : null;
    }

    /// <summary>
    /// Simulates TryQuarantineFile. When simulateFailure is true, always returns false
    /// without touching the filesystem (models disk-full, permission denied, etc.).
    /// </summary>
    private static bool TryQuarantineSimulated(
        string srcPath, string fileName, bool simulateFailure)
    {
        if (simulateFailure) return false;

        try
        {
            var qDir = Path.Combine(Path.GetTempPath(), "qtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qDir);
            File.Move(srcPath, Path.Combine(qDir, fileName), overwrite: true);
            return true;
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SCENARIO A — UNAVAILABLE VOLUME
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void A1_VolumeNotMounted_Cancel_ResultIsPartial()
    {
        const string platformId = "a1p";
        const string dlId       = "a1d";
        var (catalog, store, allSpecs) = ProvisionStore("a1", platformId, dlId, 4);
        var specsA = allSpecs.Take(2).ToList();
        var specsB = allSpecs.Skip(2).ToList();

        var volA = AddVolume(catalog, "vol-a1-A", platformId, dlId, "present", specsA);
        var volB = AddVolume(catalog, "vol-a1-B", platformId, dlId, "present", specsB);

        WriteVolumeFiles("vol-a1-A", specsA);
        WriteVolumeFiles("vol-a1-B", specsB);

        // Vol B appears not-mounted on first attempt; user cancels
        var result = SimulateVerifyAll(catalog, store, new[] { volA, volB }.ToList(),
            firstAttemptFailVolIds: new HashSet<string> { volB.Id },
            cancelWhenNotMounted: true);

        Assert.True(result.Cancelled);
    }

    [Fact]
    public void A1_Cancel_PriorVolumeChangesPreserved()
    {
        const string platformId = "a1bp";
        const string dlId       = "a1bd";
        var (catalog, store, allSpecs) = ProvisionStore("a1b", platformId, dlId, 4);
        var specsA = allSpecs.Take(2).ToList();
        var specsB = allSpecs.Skip(2).ToList();

        var volA = AddVolume(catalog, "vol-a1b-A", platformId, dlId, "present", specsA);
        var volB = AddVolume(catalog, "vol-a1b-B", platformId, dlId, "present", specsB);

        WriteVolumeFiles("vol-a1b-A", specsA);
        WriteVolumeFiles("vol-a1b-B", specsB);

        SimulateVerifyAll(catalog, store, new[] { volA, volB }.ToList(),
            firstAttemptFailVolIds: new HashSet<string> { volB.Id },
            cancelWhenNotMounted: true);

        // Volume A was processed before cancel — its artifacts must be present
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(specsA, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void A1_Cancel_ArchiveChangesAlsoPreserved()
    {
        const string platformId = "a1cp";
        const string dlId       = "a1cd";
        var (catalog, store, allSpecs) = ProvisionStore("a1c", platformId, dlId, 4);
        var archiveSpecs = allSpecs.Take(2).ToList();
        var volSpecs     = allSpecs.Skip(2).ToList();

        // Archive artifacts (not linked to any volume)
        WriteArchiveFiles(archiveSpecs);

        var vol = AddVolume(catalog, "vol-a1c", platformId, dlId, "present", volSpecs);
        WriteVolumeFiles("vol-a1c", volSpecs);

        // Cancel at volume index 0
        SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            firstAttemptFailVolIds: new HashSet<string> { vol.Id },
            cancelWhenNotMounted: true);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        // Archive phase ran before volume phase — changes must be committed
        Assert.All(archiveSpecs, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void A2_Retry_VolumeBecomesAccessible_VerificationContinues()
    {
        const string platformId = "a2p";
        const string dlId       = "a2d";
        var (catalog, store, allSpecs) = ProvisionStore("a2", platformId, dlId, 4);
        var specsA = allSpecs.Take(2).ToList();
        var specsB = allSpecs.Skip(2).ToList();

        var volA = AddVolume(catalog, "vol-a2-A", platformId, dlId, "present", specsA);
        var volB = AddVolume(catalog, "vol-a2-B", platformId, dlId, "present", specsB);

        WriteVolumeFiles("vol-a2-A", specsA);
        WriteVolumeFiles("vol-a2-B", specsB);  // dir exists — retry will find it

        // Vol B fails on first attempt, succeeds on retry (dir actually exists)
        var result = SimulateVerifyAll(catalog, store, new[] { volA, volB }.ToList(),
            firstAttemptFailVolIds: new HashSet<string> { volB.Id },
            cancelWhenNotMounted: false);   // retry, not cancel

        Assert.False(result.Cancelled);
        Assert.Equal(2, result.VerifiedVols);  // both volumes processed
        Assert.Equal(4, result.TotalVerified); // all 4 artifacts verified
    }

    [Fact]
    public void A2_Retry_PriorVolumeWorkUndamaged()
    {
        const string platformId = "a2bp";
        const string dlId       = "a2bd";
        var (catalog, store, allSpecs) = ProvisionStore("a2b", platformId, dlId, 4);
        var specsA = allSpecs.Take(2).ToList();
        var specsB = allSpecs.Skip(2).ToList();

        var volA = AddVolume(catalog, "vol-a2b-A", platformId, dlId, "present", specsA);
        var volB = AddVolume(catalog, "vol-a2b-B", platformId, dlId, "present", specsB);

        WriteVolumeFiles("vol-a2b-A", specsA);
        WriteVolumeFiles("vol-a2b-B", specsB);

        SimulateVerifyAll(catalog, store, new[] { volA, volB }.ToList(),
            firstAttemptFailVolIds: new HashSet<string> { volB.Id },
            cancelWhenNotMounted: false);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        // Both volumes verified — all artifacts must be present
        Assert.All(allSpecs, s => Assert.Equal("present", statuses[s.DaId]));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SCENARIO B — HASH MISMATCH
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void B1_ArchiveMismatch_DetectedCorrectly_WithoutStatusChange()
    {
        // Mismatch without quarantine: artifact status intentionally left unchanged.
        // The important invariant is (a) mismatch is counted and (b) artifact not promoted to "present"
        // via this verification path (it was already "present" from ingest, and stays unchanged).
        const string platformId = "b1p";
        const string dlId       = "b1d";
        var (catalog, store, specs) = ProvisionStore("b1", platformId, dlId, 2);
        WriteArchiveFiles(specs);

        // Corrupt specs[1]
        var corruptPath = Path.Combine(_archiveRoot,
            specs[1].RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(corruptPath, System.Text.Encoding.UTF8.GetBytes("CORRUPTED"));

        var result = SimulateVerifyAll(catalog, store, new List<VolumeRecord>(),
            quarantineMismatch: false);

        Assert.Equal(1, result.ArchiveVerified);
        Assert.Equal(1, result.ArchiveMismatch);
        Assert.Equal(0, result.ArchiveQuarantined);
        // specs[0] correctly transitions to present (overwrite is idempotent here)
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", statuses[specs[0].DaId]);
        // specs[1]: no quarantine → status left unchanged (still "present" from ingest)
        // This is intentional: can't mark missing without removing the bad file.
        Assert.Equal("present", statuses[specs[1].DaId]);
    }

    [Fact]
    public void B2_VolumeMismatch_LostVolumeNotRestored()
    {
        const string platformId = "b2p";
        const string dlId       = "b2d";
        var (catalog, store, specs) = ProvisionStore("b2", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-b2", platformId, dlId, "lost", specs);

        // Write specs[0] correctly; specs[1] corrupted
        var root = Path.Combine(_volumesDir, "vol-b2");
        Directory.CreateDirectory(Path.Combine(root, specs[0].ReleaseName));
        File.WriteAllBytes(Path.Combine(root, specs[0].ReleaseName, specs[0].FileName), specs[0].Content);
        Directory.CreateDirectory(Path.Combine(root, specs[1].ReleaseName));
        File.WriteAllBytes(Path.Combine(root, specs[1].ReleaseName, specs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("BAD_CONTENT"));

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            quarantineMismatch: false);

        Assert.Equal(0, result.RestoredVols);
        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("lost", updated.Status);
        Assert.Equal("crit", updated.Health);
    }

    [Fact]
    public void B3_MismatchQuarantineSuccess_ArtifactMarkedMissing()
    {
        const string platformId = "b3p";
        const string dlId       = "b3d";
        var (catalog, store, specs) = ProvisionStore("b3", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-b3", platformId, dlId, "present", specs);

        var root = WriteVolumeFiles("vol-b3", specs);
        // Corrupt specs[1] after writing
        File.WriteAllBytes(Path.Combine(root, specs[1].ReleaseName, specs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("WRONG"));

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            quarantineMismatch: true, simulateQuarantineFailure: false);

        Assert.Equal(1, result.TotalQuarantined);
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", statuses[specs[0].DaId]);
        Assert.Equal("missing", statuses[specs[1].DaId]);  // quarantine succeeded → missing
    }

    [Fact]
    public void B3_MismatchQuarantineSuccess_ReleaseRecalculated()
    {
        const string platformId = "b3rp";
        const string dlId       = "b3rd";
        var (catalog, store, allSpecs) = ProvisionStore("b3r", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-b3r", platformId, dlId, "present", allSpecs);

        var root = WriteVolumeFiles("vol-b3r", allSpecs);
        File.WriteAllBytes(Path.Combine(root, allSpecs[1].ReleaseName, allSpecs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("CORRUPTED"));

        // Pre-mark release status as "present" so we can observe it being recalculated
        store.BatchUpdateDerivedArtifactStatus(allSpecs.Select(s => s.DaId).ToList(), "present");
        store.RecalculateReleaseStatusForArtifacts(allSpecs.Select(s => s.DaId).ToList());
        var relsBefore = store.LoadReleases().ToDictionary(r => r.Name, r => r.Status);
        // After quarantine + recalculate, the release for specs[1] should no longer be present
        Assert.Equal("present", relsBefore[$"Rel b3r 0"]);
        Assert.Equal("present", relsBefore[$"Rel b3r 1"]);

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            quarantineMismatch: true, simulateQuarantineFailure: false);

        var relsAfter = store.LoadReleases().ToDictionary(r => r.Name, r => r.Status);
        Assert.Equal("present", relsAfter[$"Rel b3r 0"]);
        Assert.NotEqual("present", relsAfter[$"Rel b3r 1"]);
    }

    [Fact]
    public void B4_MismatchQuarantineFail_StatusUnchanged_VolumeHealthCrit()
    {
        // KEY INVARIANT: When quarantine fails, the artifact status is left unchanged
        // (production comment: "state unknown — can't mark missing without removing file").
        // Volume health MUST still be CRIT because mismatch count > 0.
        const string platformId = "b4p";
        const string dlId       = "b4d";
        var (catalog, store, specs) = ProvisionStore("b4", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-b4", platformId, dlId, "present", specs);

        var root = WriteVolumeFiles("vol-b4", specs);
        File.WriteAllBytes(Path.Combine(root, specs[1].ReleaseName, specs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("CORRUPTED"));

        // Explicitly set specs[1] to "present" so we can check it stays that way
        store.BatchUpdateDerivedArtifactStatus(new[] { specs[1].DaId }, "present");

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            quarantineMismatch: true, simulateQuarantineFailure: true);

        Assert.Equal(1, result.TotalMismatch);
        Assert.Equal(1, result.TotalQuarantineFailed);

        // Status unchanged because quarantine failed
        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", statuses[specs[1].DaId]);   // unchanged, not marked missing

        // But volume health MUST be crit — mismatch was counted
        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("crit", updated.Health);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SCENARIO C — QUARANTINE FAILURE
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void C1_QuarantineFailure_OperationContinues_OtherArtifactsVerified()
    {
        // A quarantine failure on artifact N must not abort processing of artifact N+1.
        const string platformId = "c1p";
        const string dlId       = "c1d";
        var (catalog, store, allSpecs) = ProvisionStore("c1", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-c1", platformId, dlId, "present", allSpecs);

        var root = WriteVolumeFiles("vol-c1", allSpecs);
        // Corrupt specs[1] — quarantine will fail
        File.WriteAllBytes(Path.Combine(root, allSpecs[1].ReleaseName, allSpecs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("BAD"));

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            quarantineMismatch: true, simulateQuarantineFailure: true);

        // specs[0] and specs[2] must still be verified despite the quarantine failure on specs[1]
        Assert.Equal(2, result.TotalVerified);
        Assert.Equal(1, result.TotalMismatch);
        Assert.Equal(1, result.TotalQuarantineFailed);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public void C2_QuarantineFailure_NoDBDesync_ValidArtifactsCommitted()
    {
        const string platformId = "c2p";
        const string dlId       = "c2d";
        var (catalog, store, allSpecs) = ProvisionStore("c2", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-c2", platformId, dlId, "present", allSpecs);

        var root = WriteVolumeFiles("vol-c2", allSpecs);
        File.WriteAllBytes(Path.Combine(root, allSpecs[1].ReleaseName, allSpecs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("CORRUPTED"));

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            quarantineMismatch: true, simulateQuarantineFailure: true);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        // Valid artifacts committed correctly
        Assert.Equal("present", statuses[allSpecs[0].DaId]);
        Assert.Equal("present", statuses[allSpecs[2].DaId]);
        // Quarantine-failed mismatch: status unchanged
        Assert.Equal("present", statuses[allSpecs[1].DaId]);
    }

    [Fact]
    public void C3_QuarantineFailure_VolumeHealthCrit_NotOk()
    {
        const string platformId = "c3p";
        const string dlId       = "c3d";
        var (catalog, store, specs) = ProvisionStore("c3", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-c3", platformId, dlId, "present", specs);

        var root = WriteVolumeFiles("vol-c3", specs);
        File.WriteAllBytes(Path.Combine(root, specs[0].ReleaseName, specs[0].FileName),
            System.Text.Encoding.UTF8.GetBytes("BAD"));

        SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            quarantineMismatch: true, simulateQuarantineFailure: true);

        // Mismatch is always counted regardless of quarantine outcome
        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("crit", updated.Health);
    }

    [Fact]
    public void C4_QuarantineFailure_ArchivePhase_StatusUnchanged_MismatchCounted()
    {
        const string platformId = "c4p";
        const string dlId       = "c4d";
        var (catalog, store, specs) = ProvisionStore("c4", platformId, dlId, 2);
        // Archive-scoped (no volume)
        WriteArchiveFiles(specs);

        var corruptPath = Path.Combine(_archiveRoot,
            specs[1].RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(corruptPath, System.Text.Encoding.UTF8.GetBytes("CORRUPT"));

        store.BatchUpdateDerivedArtifactStatus(new[] { specs[1].DaId }, "present");

        var result = SimulateVerifyAll(catalog, store, new List<VolumeRecord>(),
            quarantineMismatch: true, simulateQuarantineFailure: true);

        Assert.Equal(1, result.ArchiveMismatch);
        Assert.Equal(1, result.ArchiveQuarantineFailed);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", statuses[specs[0].DaId]);   // verified ok
        Assert.Equal("present", statuses[specs[1].DaId]);   // quarantine failed → unchanged
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SCENARIO D — PARTIAL MULTI-VOLUME RUN
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void D1_Archive_Vol1_Ok_Vol2_Cancelled_ArchivePreserved()
    {
        const string platformId = "d1p";
        const string dlId       = "d1d";
        var (catalog, store, allSpecs) = ProvisionStore("d1", platformId, dlId, 6);
        var archiveSpecs = allSpecs.Take(2).ToList();
        var vol1Specs    = allSpecs.Skip(2).Take(2).ToList();
        var vol2Specs    = allSpecs.Skip(4).ToList();

        WriteArchiveFiles(archiveSpecs);
        var vol1 = AddVolume(catalog, "vol-d1-1", platformId, dlId, "present", vol1Specs);
        var vol2 = AddVolume(catalog, "vol-d1-2", platformId, dlId, "present", vol2Specs);
        WriteVolumeFiles("vol-d1-1", vol1Specs);
        WriteVolumeFiles("vol-d1-2", vol2Specs);

        // Cancel before processing vol2
        var result = SimulateVerifyAll(catalog, store,
            new[] { vol1, vol2 }.ToList(), cancelAtVolIndex: 1);

        Assert.True(result.Cancelled);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(archiveSpecs, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void D2_Archive_Vol1_Ok_Vol2_Cancelled_Vol1Preserved()
    {
        const string platformId = "d2p";
        const string dlId       = "d2d";
        var (catalog, store, allSpecs) = ProvisionStore("d2", platformId, dlId, 6);
        var archiveSpecs = allSpecs.Take(2).ToList();
        var vol1Specs    = allSpecs.Skip(2).Take(2).ToList();
        var vol2Specs    = allSpecs.Skip(4).ToList();

        WriteArchiveFiles(archiveSpecs);
        var vol1 = AddVolume(catalog, "vol-d2-1", platformId, dlId, "present", vol1Specs);
        var vol2 = AddVolume(catalog, "vol-d2-2", platformId, dlId, "present", vol2Specs);
        WriteVolumeFiles("vol-d2-1", vol1Specs);
        WriteVolumeFiles("vol-d2-2", vol2Specs);

        SimulateVerifyAll(catalog, store, new[] { vol1, vol2 }.ToList(), cancelAtVolIndex: 1);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(vol1Specs, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void D3_NoStaleRollback_Vol2NotScanned_Unchanged()
    {
        // Vol2 was never reached — its artifacts must stay at their pre-run state.
        const string platformId = "d3p";
        const string dlId       = "d3d";
        var (catalog, store, allSpecs) = ProvisionStore("d3", platformId, dlId, 4);
        var vol1Specs = allSpecs.Take(2).ToList();
        var vol2Specs = allSpecs.Skip(2).ToList();

        var vol1 = AddVolume(catalog, "vol-d3-1", platformId, dlId, "present", vol1Specs);
        var vol2 = AddVolume(catalog, "vol-d3-2", platformId, dlId, "present", vol2Specs);
        WriteVolumeFiles("vol-d3-1", vol1Specs);
        WriteVolumeFiles("vol-d3-2", vol2Specs);

        // Establish a known pre-run state for vol2 artifacts
        store.BatchUpdateDerivedArtifactStatus(vol2Specs.Select(s => s.DaId).ToList(), "missing");

        SimulateVerifyAll(catalog, store, new[] { vol1, vol2 }.ToList(), cancelAtVolIndex: 1);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        // Vol2 never scanned → pre-run "missing" state preserved
        Assert.All(vol2Specs, s => Assert.Equal("missing", statuses[s.DaId]));
    }

    [Fact]
    public void D4_Partial_Vol1Health_SetBeforeVol2_Independent()
    {
        const string platformId = "d4p";
        const string dlId       = "d4d";
        var (catalog, store, allSpecs) = ProvisionStore("d4", platformId, dlId, 4);
        var vol1Specs = allSpecs.Take(2).ToList();
        var vol2Specs = allSpecs.Skip(2).ToList();

        var vol1 = AddVolume(catalog, "vol-d4-1", platformId, dlId, "present", vol1Specs);
        var vol2 = AddVolume(catalog, "vol-d4-2", platformId, dlId, "present", vol2Specs);
        WriteVolumeFiles("vol-d4-1", vol1Specs);
        // vol2 is cancelled at index 1

        SimulateVerifyAll(catalog, store, new[] { vol1, vol2 }.ToList(), cancelAtVolIndex: 1);

        var updatedVol1 = catalog.GetVolumes().Single(v => v.Id == vol1.Id);
        // Vol1 was fully verified — health must be ok regardless of vol2 outcome
        Assert.Equal("ok", updatedVol1.Health);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SCENARIO E — LOST RESTORE SAFETY
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void E1_LostVolume_FullyValid_Restored_WithCorrectLocation()
    {
        const string platformId = "e1p";
        const string dlId       = "e1d";
        var (catalog, store, specs) = ProvisionStore("e1", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-e1", platformId, dlId, "lost", specs);
        WriteVolumeFiles("vol-e1", specs);

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        Assert.Equal(1, result.RestoredVols);
        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("present",   updated.Status);
        Assert.Equal("ok",        updated.Health);
        var loc = catalog.GetCurrentLocation(vol.Id);
        Assert.NotNull(loc);
        Assert.Equal("workspace", loc!.LocationType);
    }

    [Fact]
    public void E2_LostVolume_OneMismatch_NotRestored()
    {
        const string platformId = "e2p";
        const string dlId       = "e2d";
        var (catalog, store, specs) = ProvisionStore("e2", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-e2", platformId, dlId, "lost", specs);

        var root = WriteVolumeFiles("vol-e2", specs);
        // Corrupt one file
        File.WriteAllBytes(Path.Combine(root, specs[2].ReleaseName, specs[2].FileName),
            System.Text.Encoding.UTF8.GetBytes("CORRUPT"));

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        Assert.Equal(0, result.RestoredVols);
        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("lost", updated.Status);
        Assert.Null(catalog.GetCurrentLocation(vol.Id));
    }

    [Fact]
    public void E3_LostVolume_Cancelled_BeforeProcessing_RemainsLost()
    {
        const string platformId = "e3p";
        const string dlId       = "e3d";
        var (catalog, store, allSpecs) = ProvisionStore("e3", platformId, dlId, 4);
        var vol1Specs    = allSpecs.Take(2).ToList();
        var lostVolSpecs = allSpecs.Skip(2).ToList();

        var vol1    = AddVolume(catalog, "vol-e3-1",    platformId, dlId, "present", vol1Specs);
        var lostVol = AddVolume(catalog, "vol-e3-lost", platformId, dlId, "lost",    lostVolSpecs);

        WriteVolumeFiles("vol-e3-1",    vol1Specs);
        WriteVolumeFiles("vol-e3-lost", lostVolSpecs);  // files exist — but we cancel before reaching it

        // Cancel before reaching the lost volume (index 1)
        SimulateVerifyAll(catalog, store, new[] { vol1, lostVol }.ToList(), cancelAtVolIndex: 1);

        var updated = catalog.GetVolumes().Single(v => v.Id == lostVol.Id);
        Assert.Equal("lost", updated.Status);  // no false promotion
        Assert.Null(catalog.GetCurrentLocation(lostVol.Id));
    }

    [Fact]
    public void E4_LostVolume_MismatchWithQuarantineSuccess_NotRestored()
    {
        // Even if quarantine succeeds (file removed), volume must not be restored
        // because volMismatch > 0 → volClean = false → restore condition fails.
        const string platformId = "e4p";
        const string dlId       = "e4d";
        var (catalog, store, specs) = ProvisionStore("e4", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-e4", platformId, dlId, "lost", specs);

        var root = WriteVolumeFiles("vol-e4", specs);
        File.WriteAllBytes(Path.Combine(root, specs[1].ReleaseName, specs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("BAD_CONTENT"));

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            quarantineMismatch: true, simulateQuarantineFailure: false);

        Assert.Equal(0, result.RestoredVols);
        var updated = catalog.GetVolumes().Single(v => v.Id == vol.Id);
        Assert.Equal("lost", updated.Status);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SCENARIO F — RESULT FLAG CONSISTENCY
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void F1_FullSuccess_Cancelled_IsFalse()
    {
        const string platformId = "f1p";
        const string dlId       = "f1d";
        var (catalog, store, specs) = ProvisionStore("f1", platformId, dlId, 3);
        var vol = AddVolume(catalog, "vol-f1", platformId, dlId, "present", specs);
        WriteVolumeFiles("vol-f1", specs);

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList());

        Assert.False(result.Cancelled);
        Assert.Equal(1, result.VerifiedVols);
        Assert.Equal(0, result.SkippedVols);
    }

    [Fact]
    public void F2_CancelAtFirst_Cancelled_IsTrue_VerifiedVols_Zero()
    {
        const string platformId = "f2p";
        const string dlId       = "f2d";
        var (catalog, store, specs) = ProvisionStore("f2", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-f2", platformId, dlId, "present", specs);
        WriteVolumeFiles("vol-f2", specs);

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList(), cancelAtVolIndex: 0);

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.VerifiedVols);
    }

    [Fact]
    public void F3_CancelAfterSuccessfulVol_Cancelled_True_WithPriorWork()
    {
        const string platformId = "f3p";
        const string dlId       = "f3d";
        var (catalog, store, allSpecs) = ProvisionStore("f3", platformId, dlId, 4);
        var vol1Specs = allSpecs.Take(2).ToList();
        var vol2Specs = allSpecs.Skip(2).ToList();

        var vol1 = AddVolume(catalog, "vol-f3-1", platformId, dlId, "present", vol1Specs);
        var vol2 = AddVolume(catalog, "vol-f3-2", platformId, dlId, "present", vol2Specs);
        WriteVolumeFiles("vol-f3-1", vol1Specs);
        WriteVolumeFiles("vol-f3-2", vol2Specs);

        var result = SimulateVerifyAll(catalog, store,
            new[] { vol1, vol2 }.ToList(), cancelAtVolIndex: 1);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.VerifiedVols);   // vol1 processed

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.All(vol1Specs, s => Assert.Equal("present", statuses[s.DaId]));
    }

    [Fact]
    public void F4_SkippedVol_CountedCorrectly_NotCancelled()
    {
        // A volume with no root (not mounted, not cancelled — just skipped after retry fails)
        const string platformId = "f4p";
        const string dlId       = "f4d";
        var (catalog, store, specs) = ProvisionStore("f4", platformId, dlId, 2);
        var vol = AddVolume(catalog, "vol-f4-inaccessible", platformId, dlId, "present", specs);
        // Volume dir not created → root resolution fails → skipped, not cancelled

        var result = SimulateVerifyAll(catalog, store, new[] { vol }.ToList(),
            cancelWhenNotMounted: false);   // retry, still not found → skip

        Assert.False(result.Cancelled);
        Assert.Equal(1, result.SkippedVols);
        Assert.Equal(0, result.VerifiedVols);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CROSS-SCENARIO — COMBINED FAILURE CONDITIONS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void X1_QuarantineFailure_And_Cancel_BothHandledCoherently()
    {
        // Archive: quarantine fails. Vol1: verified. Vol2: cancelled.
        // Assert: archive mismatch counted, vol1 committed, vol2 not touched.
        const string platformId = "x1p";
        const string dlId       = "x1d";
        var (catalog, store, allSpecs) = ProvisionStore("x1", platformId, dlId, 6);
        var archiveSpecs = allSpecs.Take(2).ToList();
        var vol1Specs    = allSpecs.Skip(2).Take(2).ToList();
        var vol2Specs    = allSpecs.Skip(4).ToList();

        WriteArchiveFiles(archiveSpecs);
        // Corrupt archive specs[1]
        var corruptPath = Path.Combine(_archiveRoot,
            archiveSpecs[1].RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(corruptPath, System.Text.Encoding.UTF8.GetBytes("CORRUPT"));

        var vol1 = AddVolume(catalog, "vol-x1-1", platformId, dlId, "present", vol1Specs);
        var vol2 = AddVolume(catalog, "vol-x1-2", platformId, dlId, "present", vol2Specs);
        WriteVolumeFiles("vol-x1-1", vol1Specs);
        WriteVolumeFiles("vol-x1-2", vol2Specs);

        store.BatchUpdateDerivedArtifactStatus(vol2Specs.Select(s => s.DaId).ToList(), "missing");

        var result = SimulateVerifyAll(catalog, store, new[] { vol1, vol2 }.ToList(),
            quarantineMismatch: true, simulateQuarantineFailure: true,
            cancelAtVolIndex: 1);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.ArchiveMismatch);
        Assert.Equal(1, result.ArchiveQuarantineFailed);

        var statuses = store.GetDerivedArtifacts().ToDictionary(d => d.Id, d => d.Status);
        Assert.Equal("present", statuses[archiveSpecs[0].DaId]);  // archive ok
        Assert.All(vol1Specs, s => Assert.Equal("present", statuses[s.DaId]));  // vol1 committed
        Assert.All(vol2Specs, s => Assert.Equal("missing",  statuses[s.DaId]));  // vol2 unchanged
    }

    [Fact]
    public void X2_LostRestore_And_QuarantineFailure_SameRun_BothHandled()
    {
        // Vol1: LOST, all valid → restored.
        // Vol2: present, has mismatch, quarantine fails → health CRIT, not lost.
        const string platformId = "x2p";
        const string dlId       = "x2d";
        var (catalog, store, allSpecs) = ProvisionStore("x2", platformId, dlId, 4);
        var vol1Specs = allSpecs.Take(2).ToList();
        var vol2Specs = allSpecs.Skip(2).ToList();

        var vol1 = AddVolume(catalog, "vol-x2-1", platformId, dlId, "lost",    vol1Specs);
        var vol2 = AddVolume(catalog, "vol-x2-2", platformId, dlId, "present", vol2Specs);

        WriteVolumeFiles("vol-x2-1", vol1Specs);
        var root2 = WriteVolumeFiles("vol-x2-2", vol2Specs);
        File.WriteAllBytes(Path.Combine(root2, vol2Specs[1].ReleaseName, vol2Specs[1].FileName),
            System.Text.Encoding.UTF8.GetBytes("CORRUPT"));

        var result = SimulateVerifyAll(catalog, store, new[] { vol1, vol2 }.ToList(),
            quarantineMismatch: true, simulateQuarantineFailure: true);

        Assert.Equal(1, result.RestoredVols);
        var updatedVol1 = catalog.GetVolumes().Single(v => v.Id == vol1.Id);
        var updatedVol2 = catalog.GetVolumes().Single(v => v.Id == vol2.Id);
        Assert.Equal("present", updatedVol1.Status);
        Assert.Equal("ok",      updatedVol1.Health);
        Assert.Equal("present", updatedVol2.Status);  // was present, stays present
        Assert.Equal("crit",    updatedVol2.Health);  // mismatch counted
    }
}
