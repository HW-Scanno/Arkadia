using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Filesystem tests for the real <see cref="ResumableStagingDetector"/> — the
/// read-only detector that decides which wanted releases with complete staging
/// should be routed back through the transform path after an interrupted run.
/// Tests call production logic and use the production folder-naming helper.
/// </summary>
public sealed class ResumableStagingDetectorTests : IDisposable
{
    private readonly string _root;
    private readonly string _stagingRoot;

    public ResumableStagingDetectorTests()
    {
        _root        = Path.Combine(Path.GetTempPath(), "ArkResume_" + Guid.NewGuid().ToString("N")[..8]);
        _stagingRoot = Path.Combine(_root, "staging", "ps2", "ps2-redump");
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Creates a staging folder for a release using the PRODUCTION folder-naming rule.</summary>
    private string CreateStaged(string releaseName, params string[] files)
    {
        var folder = Path.Combine(_stagingRoot, IngestionPaths.SafeFolderName(releaseName));
        Directory.CreateDirectory(folder);
        foreach (var f in files)
            File.WriteAllBytes(Path.Combine(folder, f), new byte[] { 1, 2, 3 });
        return folder;
    }

    private static ResumeReleaseInput Rel(string id, string name, string status, params string[] expected) =>
        new(id, name, status, expected.ToList());

    private ResumableStagingResult Detect(params ResumeReleaseInput[] releases) =>
        ResumableStagingDetector.Detect(_stagingRoot, releases.ToList());

    // ── 1 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_WantedCompleteStagingWithoutDerived_IsRoutedForTransform()
    {
        CreateStaged("Sonic Adventure (USA)", "disc.cue", "disc.bin");

        var result = Detect(Rel("r1", "Sonic Adventure (USA)", "missing", "disc.cue", "disc.bin"));

        Assert.Contains("r1", result.ResumableReleaseIds);
    }

    // ── 2 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_WantedIncompleteStaging_IsNotRoutedForTransform()
    {
        // Only the .cue was staged; the .bin is missing → incomplete.
        CreateStaged("Sonic Adventure (USA)", "disc.cue");

        var result = Detect(Rel("r1", "Sonic Adventure (USA)", "missing", "disc.cue", "disc.bin"));

        Assert.DoesNotContain("r1", result.ResumableReleaseIds);
        Assert.Contains(result.Skipped, s => s.ReleaseId == "r1" && s.Reason == "incomplete-staging");
    }

    // ── 3 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_WantedCompleteStagingWithValidDerived_IsNotRoutedForTransform()
    {
        // 'present' ⟺ a valid derived artifact already committed → do not re-transform.
        CreateStaged("Mario", "mario.iso");

        var result = Detect(Rel("r1", "Mario", "present", "mario.iso"));

        Assert.DoesNotContain("r1", result.ResumableReleaseIds);
        Assert.Contains(result.Skipped, s => s.ReleaseId == "r1" && s.Reason == "already-present");
    }

    // ── 4 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_UnwantedCompleteStaging_IsNotRoutedForTransform()
    {
        CreateStaged("Vetoed Game", "game.iso");

        var result = Detect(Rel("r1", "Vetoed Game", "unwanted", "game.iso"));

        Assert.DoesNotContain("r1", result.ResumableReleaseIds);
        Assert.Contains(result.Skipped, s => s.ReleaseId == "r1" && s.Reason == "unwanted");
    }

    // ── 5 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_AmbiguousStagingFolder_IsNotRoutedForTransform()
    {
        // Two releases sanitize to the same folder ("Zelda"): the mapping is ambiguous.
        Assert.Equal(
            IngestionPaths.SafeFolderName("Zelda"),
            IngestionPaths.SafeFolderName("  Zelda  "));
        CreateStaged("Zelda", "zelda.iso");

        var result = Detect(
            Rel("r1", "Zelda",       "missing", "zelda.iso"),
            Rel("r2", "  Zelda  ",   "missing", "zelda.iso"));

        Assert.DoesNotContain("r1", result.ResumableReleaseIds);
        Assert.DoesNotContain("r2", result.ResumableReleaseIds);
        Assert.Contains(result.Skipped, s => s.ReleaseId == "r1" && s.Reason == "ambiguous-folder");
    }

    // ── 6 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_OrphanStagingFolder_IsNotRoutedForTransform()
    {
        // A staging folder exists for a name with no matching release in the DB set.
        CreateStaged("Ghost Release", "ghost.iso");

        // The only known release has no staging of its own.
        var result = Detect(Rel("r1", "Real Release", "missing", "real.iso"));

        Assert.Empty(result.ResumableReleaseIds);
        Assert.Contains(result.Skipped, s => s.ReleaseId == "r1" && s.Reason == "no-staging");
    }

    // ── 7 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_ResumableStaging_DoesNotDeleteStagingBeforeTransform()
    {
        var folder = CreateStaged("Sonic Adventure (USA)", "disc.cue", "disc.bin");

        var result = Detect(Rel("r1", "Sonic Adventure (USA)", "missing", "disc.cue", "disc.bin"));

        // Detection is a routing decision only — it must never touch the files.
        Assert.Contains("r1", result.ResumableReleaseIds);
        Assert.True(File.Exists(Path.Combine(folder, "disc.cue")));
        Assert.True(File.Exists(Path.Combine(folder, "disc.bin")));
    }

    // ── 8 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_ResumableStaging_DoesNotMarkPresentBeforeDerivedCommit()
    {
        // The detector has no DB access and performs no writes: it only returns a
        // decision. Nothing is promoted, transformed, or marked present here — that
        // stays the pipeline's job, gated on a verified derived commit.
        CreateStaged("Sonic Adventure (USA)", "disc.iso");
        var before = Directory.GetFileSystemEntries(_stagingRoot, "*", SearchOption.AllDirectories).Length;

        var result = Detect(Rel("r1", "Sonic Adventure (USA)", "missing", "disc.iso"));
        var after  = Directory.GetFileSystemEntries(_stagingRoot, "*", SearchOption.AllDirectories).Length;

        Assert.Contains("r1", result.ResumableReleaseIds);
        Assert.Equal(before, after);          // no side effects
    }

    // ── 9 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ingestion_ResumableStaging_UsesProductionHelperNotTestLocalPathRule()
    {
        // A name that the production sanitizer changes (trailing spaces trimmed).
        const string name = "Metroid Prime  ";
        Assert.NotEqual(name, IngestionPaths.SafeFolderName(name));

        // Staging folder is created at the PRODUCTION-sanitized path.
        CreateStaged(name, "prime.iso");

        var result = Detect(Rel("r1", name, "missing", "prime.iso"));

        // Detected only because the detector derives the folder with the same
        // production helper — a raw-name path rule would miss the sanitized folder.
        Assert.Contains("r1", result.ResumableReleaseIds);
    }
}
