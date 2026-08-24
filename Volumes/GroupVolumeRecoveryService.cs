using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>Outcome of a Group volume interrupted-build recovery.</summary>
public enum GroupVolumeRecoveryStatus
{
    /// <summary>The volume verified fully healthy and was promoted init → present with its workspace location.</summary>
    Finalized,
    /// <summary>Verification ran but the volume is not fully satisfied (missing / mismatch / unresolved) — left INIT.</summary>
    NotFinalized,
    /// <summary>The volume has no assigned artifacts — not an interrupted build. Nothing done.</summary>
    NothingToRecover,
    /// <summary>The volume is not INIT (already finalized, or lost) — recovery is a no-op. Nothing done.</summary>
    NotInterrupted,
}

/// <summary>Result of <see cref="GroupVolumeRecoveryService.Recover"/>: status, the aggregated verify result, and a block reason.</summary>
public sealed record GroupVolumeRecoveryResult(
    GroupVolumeRecoveryStatus Status,
    VolumeVerifyResult?       Verify,
    string?                   BlockReason);

/// <summary>
/// Recovers a Group volume whose Apply &amp; Build finalization was interrupted by a software exception (the real
/// case: physical files + <c>volume_artifacts</c> + <c>actual_size_bytes</c> were committed, but a "too many SQL
/// variables" throw during the per-leaf status finalization prevented the <c>init → present</c> + workspace-location
/// commit — leaving the volume stuck INIT with materialized contents).
///
/// <para>This is NOT a second verifier: it REUSES <see cref="GroupVolumeVerifyOrchestrator.Verify"/> (which reuses
/// the Single <see cref="VolumeVerifyService.VerifyCore"/>) to hash-verify every assigned leaf against authoritative
/// DB SHA-1s, then adds only the finalization seam — the exact two lifecycle writes that Build's tail (and the
/// Verify "restore from LOST" branch) already perform. It finalizes ONLY when the whole volume verifies healthy;
/// a single missing/mismatched/unresolved artifact leaves the volume INIT (health <c>crit</c>) and surfaces the
/// failure. Never trusts file existence or size alone — correctness is the SHA-1 verification.</para>
///
/// <para>Idempotent: once finalized the volume is no longer INIT, so a second call returns
/// <see cref="GroupVolumeRecoveryStatus.NotInterrupted"/> without re-verifying or re-writing. No recopy, no
/// duplicate assignments, no size double-count (Apply is never re-run; VerifyCore only reconciles genuine drift).</para>
/// </summary>
public static class GroupVolumeRecoveryService
{
    public static GroupVolumeRecoveryResult Recover(
        CatalogService                    catalog,
        string                            dataDir,
        string                            appRoot,
        VolumeRecord                      volume,
        string                            volumeRoot,
        string?                           diskId,
        IReadOnlyList<GroupLeafRecord>    leaves,
        IReadOnlyList<string>             allDatLineDbPaths,
        IProgress<FoundFileProgress>?     foundFileProgress = null,
        IProgress<VolumeVerifyProgress>?  verifyProgress    = null,
        IProgress<string>?                leafProgress      = null)
    {
        // Only an INIT volume is an interrupted build; present/lost are handled by the normal Verify / restore path.
        if (volume.Status != "init")
            return new GroupVolumeRecoveryResult(GroupVolumeRecoveryStatus.NotInterrupted, null, null);

        // An interrupted build has committed assignments. An empty INIT volume is a fresh, un-built volume.
        if (catalog.GetVolumeArtifacts(volume.Id).Count == 0)
            return new GroupVolumeRecoveryResult(GroupVolumeRecoveryStatus.NothingToRecover, null, null);

        // Authoritative hash verification — the EXISTING Group Verify Volume primitive, unchanged.
        var outcome = GroupVolumeVerifyOrchestrator.Verify(
            catalog, dataDir, volume, volumeRoot, leaves, allDatLineDbPaths,
            foundFileProgress, verifyProgress, leafProgress);

        if (outcome.BlockReason is not null)
            return new GroupVolumeRecoveryResult(GroupVolumeRecoveryStatus.NotFinalized, null, outcome.BlockReason);

        var verify = outcome.Result!;
        if (!verify.IsHealthy)
        {
            // Materially unsatisfied — never promote to present. Record unhealth; the volume stays INIT.
            catalog.UpdateVolumeHealth(volume.Id, "crit");
            return new GroupVolumeRecoveryResult(GroupVolumeRecoveryStatus.NotFinalized, verify, null);
        }

        // ── Finalization seam — identical to Build's tail / the Verify "restore from LOST" branch ──
        var wsRoot      = Path.Combine(appRoot, "volumes", Ingestion.IngestionPaths.SafeFolderName(volume.Label));
        bool isWorkspace = volumeRoot.StartsWith(wsRoot, StringComparison.OrdinalIgnoreCase);
        catalog.SetCurrentLocation(new VolumeLocationRecord
        {
            Id           = Guid.NewGuid().ToString("N"),
            VolumeId     = volume.Id,
            LocationType = isWorkspace ? "workspace" : "disk",
            DiskId       = isWorkspace ? null : diskId,
            Path         = volumeRoot,
            IsCurrent    = true,
            CreatedAt    = DateTime.UtcNow,
        });
        catalog.UpdateVolumeStatus(volume.Id, "present");
        catalog.UpdateVolumeHealth(volume.Id, "ok");

        return new GroupVolumeRecoveryResult(GroupVolumeRecoveryStatus.Finalized, verify, null);
    }
}
