using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Tests for the pre-staging redundancy guard (<see cref="RedundantIncomingPolicy"/>) that
/// prevents a lone <c>.cue</c> (or any incoming constituent file) from creating a new
/// staging folder when its release is already present with a derived artifact on disk.
///
/// This is the production decision used in ingestion Phase 4b to mark such a target
/// "satisfied" (→ Phase 8 handles the incoming file as <c>duplicate-deleted</c>). The
/// end-to-end Phase-6 staging is UI-private (<c>RunIngestionWork</c>); these cover the
/// extracted decision seam directly, with a real filesystem existence probe.
/// </summary>
public sealed class RedundantIncomingPolicyTests : IDisposable
{
    private readonly string _tmp;

    public RedundantIncomingPolicyTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ArkRedund_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    private Func<string, bool> RealProbe() =>
        rel =>
        {
            var full = Path.Combine(_tmp, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) || Directory.Exists(full);
        };

    private string MakeArtifact(string relativePath)
    {
        var full = Path.Combine(_tmp, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[] { 1, 2, 3 });
        return relativePath;
    }

    [Fact]
    public void Ingestion_CueOnlyForAlreadyPresentRelease_DoesNotCreateStagingFolder()
    {
        // Present release whose CHD already exists on disk → the incoming .cue is redundant,
        // so its target is "satisfied" and NO staging folder is created for it.
        var chd = MakeArtifact("archive/dc/redump/Sonic Adventure (USA).chd");

        var complete = RedundantIncomingPolicy.IsReleaseAlreadyComplete(
            "present", new List<string> { chd }, RealProbe());

        Assert.True(complete);   // satisfied ⇒ Phase 6 skips staging this .cue
    }

    [Fact]
    public void Ingestion_CueOnlyForAlreadyPresentRelease_IsHandledIdempotently()
    {
        // The decision is stable across repeated runs (idempotent): a present+derived
        // release stays "already complete", so re-ingesting the .cue keeps duplicate-deleting
        // it rather than re-staging — no accumulation.
        var chd = MakeArtifact("archive/psx/redump/Game/Game.chd");
        var probe = RealProbe();

        Assert.True(RedundantIncomingPolicy.IsReleaseAlreadyComplete("present", new[] { chd }, probe));
        Assert.True(RedundantIncomingPolicy.IsReleaseAlreadyComplete("present", new[] { chd }, probe));
    }

    [Fact]
    public void Ingestion_CueCompletesIncompleteBinRelease_StillAssemblesInput()
    {
        // A release that is NOT present (still being acquired) is never short-circuited —
        // the .cue must stage so it can complete the bin/cue input set.
        var chd = "archive/dc/redump/Incomplete.chd";   // not created on disk

        Assert.False(RedundantIncomingPolicy.IsReleaseAlreadyComplete(
            "missing", new List<string> { chd }, RealProbe()));
        Assert.False(RedundantIncomingPolicy.IsReleaseAlreadyComplete(
            "pending", new List<string> { chd }, RealProbe()));
    }

    [Fact]
    public void Ingestion_CueOnlyForMissingBin_DoesNotCreateMisleadingCompleteInput()
    {
        // Release is marked present but its derived artifact is physically MISSING → not
        // treated as complete, so a lone .cue is NOT accepted as a complete input; it falls
        // through to the normal (Phase 7) completeness check which requires all files.
        var missingChd = "archive/dc/redump/Ghost.chd";   // never created

        var complete = RedundantIncomingPolicy.IsReleaseAlreadyComplete(
            "present", new List<string> { missingChd }, RealProbe());

        Assert.False(complete);
    }

    [Fact]
    public void Ingestion_PresentReleaseWithNoArtifactRow_IsNotComplete()
    {
        // Present status but zero derived artifacts → not complete (nothing to be redundant to).
        Assert.False(RedundantIncomingPolicy.IsReleaseAlreadyComplete(
            "present", new List<string>(), RealProbe()));
    }

    [Fact]
    public void Ingestion_MultiFileReleaseFolderArtifact_CountsAsComplete()
    {
        // For MultiFileReleaseFolder the artifact relative_path is a DIRECTORY; the probe
        // accepts folders too, so a present foldered release is also "already complete".
        var folder = "archive/psx/redump/Some Game";
        Directory.CreateDirectory(Path.Combine(_tmp, folder.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(RedundantIncomingPolicy.IsReleaseAlreadyComplete(
            "present", new List<string> { folder }, RealProbe()));
    }

    [Fact]
    public void Ingestion_CueOnlyPackage_CompletesOnlyMissingRelease_NoStagingClutter()
    {
        // Mixed package: release A is present+derived (its .cue is redundant → no staging),
        // release B is still missing (its .cue must stage to complete it). Only B is worked.
        var chdA = MakeArtifact("archive/dc/redump/Present Game.chd");
        var chdB = "archive/dc/redump/Missing Game.chd";   // not on disk
        var probe = RealProbe();

        Assert.True(RedundantIncomingPolicy.IsReleaseAlreadyComplete("present", new[] { chdA }, probe));  // A: redundant
        Assert.False(RedundantIncomingPolicy.IsReleaseAlreadyComplete("missing", new[] { chdB }, probe)); // B: staged
    }

    // ── volume-aware Locate() ─────────────────────────────────────────────────
    // The extended seam used by Phase 4b: a release satisfied by a reachable assigned
    // volume (even with NO local archive copy) must not stage; an assigned-but-unavailable
    // volume must be quarantined rather than deleted; the local case still wins.

    private static ArtifactAvailability Local(string relPath) =>
        new(relPath, Array.Empty<VolumeAssignmentRef>());

    private static ArtifactAvailability OnVolume(string relPath, string label, string fileName, string? diskId = null) =>
        new(relPath, new[] { new VolumeAssignmentRef(label, diskId, fileName) });

    [Fact]
    public void CueOnlyForReleaseSatisfiedByLocalArchive_DoesNotStage()
    {
        var chd = MakeArtifact("archive/ps2/redump/Game A.chd");
        var sat = RedundantIncomingPolicy.Locate(
            "present", new[] { Local(chd) }, RealProbe(), _ => VolumeProbeResult.Unreachable);
        Assert.Equal(ReleaseSatisfaction.LocalArchive, sat);
    }

    [Fact]
    public void CueOnlyForReleaseSatisfiedByAssignedVolume_DoesNotStage()
    {
        // No local archive copy (moved to a volume), but the assigned volume holds the file.
        var art = OnVolume("archive/ps2/redump/Game A.chd", "VOL-1", "Game A.chd");
        var sat = RedundantIncomingPolicy.Locate(
            "present", new[] { art }, RealProbe(), _ => VolumeProbeResult.FilePresent);
        Assert.Equal(ReleaseSatisfaction.ReachableVolume, sat);
    }

    [Fact]
    public void CueOnlyForReleaseSatisfiedByAssignedVolume_IsDuplicateDeleted()
    {
        // ReachableVolume is treated exactly like LocalArchive by the caller (satisfied ⇒
        // Phase 8 duplicate-deleted). Assert it maps to the "satisfied" family, not quarantine.
        var art = OnVolume("archive/ps2/redump/Game A.chd", "VOL-1", "Game A.chd");
        var sat = RedundantIncomingPolicy.Locate(
            "present", new[] { art }, RealProbe(), _ => VolumeProbeResult.FilePresent);
        Assert.True(sat is ReleaseSatisfaction.LocalArchive or ReleaseSatisfaction.ReachableVolume);
        Assert.NotEqual(ReleaseSatisfaction.AssignedVolumeUnavailable, sat);
    }

    [Fact]
    public void CueOnlyForReleaseAssignedToUnavailableVolume_DoesNotCreateCueOnlyStaging()
    {
        // Volume unreachable → cannot confirm the copy. Not NotSatisfied (which would stage);
        // AssignedVolumeUnavailable ⇒ caller quarantines to incoming-skip, not staging.
        var art = OnVolume("archive/ps2/redump/Game A.chd", "VOL-OFFLINE", "Game A.chd", diskId: "disk-x");
        var sat = RedundantIncomingPolicy.Locate(
            "present", new[] { art }, RealProbe(), _ => VolumeProbeResult.Unreachable);
        Assert.Equal(ReleaseSatisfaction.AssignedVolumeUnavailable, sat);
    }

    [Fact]
    public void VolumeSatisfiedRelease_DoesNotRequireLocalArchiveCopy()
    {
        // The whole point: a reachable-volume satisfaction stands on its own with NO local file.
        var art = OnVolume("archive/ps2/redump/Only On Volume.chd", "VOL-2", "Only On Volume.chd");
        Assert.False(RealProbe()("archive/ps2/redump/Only On Volume.chd"));   // nothing local
        var sat = RedundantIncomingPolicy.Locate(
            "present", new[] { art }, RealProbe(), _ => VolumeProbeResult.FilePresent);
        Assert.Equal(ReleaseSatisfaction.ReachableVolume, sat);
    }

    [Fact]
    public void CueOnlyForMissingBin_DoesNotCreateMisleadingStaging()
    {
        // Not present, and no durable copy anywhere → NotSatisfied (normal completion path;
        // a lone .cue is not treated as a complete input).
        var art = OnVolume("archive/ps2/redump/Ghost.chd", "VOL-1", "Ghost.chd");
        var sat = RedundantIncomingPolicy.Locate(
            "missing", new[] { art }, RealProbe(), _ => VolumeProbeResult.FilePresent);
        Assert.Equal(ReleaseSatisfaction.NotSatisfied, sat);   // status gate: not present
    }

    [Fact]
    public void Locate_LocalArchiveWins_OverUnavailableVolume()
    {
        // A present release with both a local copy AND an unreachable volume assignment
        // resolves to LocalArchive (strongest), so it is satisfied, not quarantined.
        var chd = MakeArtifact("archive/ps2/redump/Both.chd");
        var art = OnVolume("archive/ps2/redump/Both.chd", "VOL-OFFLINE", "Both.chd", diskId: "disk-x");
        var sat = RedundantIncomingPolicy.Locate(
            "present", new[] { art }, RealProbe(), _ => VolumeProbeResult.Unreachable);
        Assert.Equal(ReleaseSatisfaction.LocalArchive, sat);
    }
}
