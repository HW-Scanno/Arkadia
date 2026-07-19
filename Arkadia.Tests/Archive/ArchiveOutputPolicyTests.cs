using System.Collections.Generic;
using System.Linq;
using Arkadia.Archive;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Archive;

/// <summary>
/// Tests for the M1a archive-output backend: form resolver, plan builder,
/// collision analyzer, and validation/fingerprint. All exercise production
/// helpers (no logic reimplemented in the test).
/// </summary>
public sealed class ArchiveOutputPolicyTests
{
    // ── Builders ────────────────────────────────────────────────────────────────

    private static ReleaseFileRecord File(string rom, string sha1 = "", string size = "1024") =>
        new() { Id = rom, ReleaseId = "r", RomName = rom, Size = size, Sha1 = sha1 };

    private static ArchiveReleaseInput Rel(
        string id, string name, string status, params ReleaseFileRecord[] files) =>
        new() { ReleaseId = id, ReleaseName = name, Status = status, Files = files.ToList() };

    private static ArchiveOutputConfig ChdConfig() => new()
    {
        PlatformId = "dc", DatLineId = "redump", StrategyType = "release_shape",
        SingleFileOutputExtension = ".chd",
    };

    private static ArchiveOutputConfig ZipConfig() => new()
    {
        PlatformId = "gba", DatLineId = "nointro", StrategyType = "release_folder",
        SingleFileOutputExtension = ".zip", FolderOutputsFolder = false,
    };

    private static ArchiveOutputConfig NoCompressionFolderConfig() => new()
    {
        PlatformId = "psx", DatLineId = "redump", StrategyType = "release_folder",
        FolderOutputsFolder = true,
    };

    private static ArchiveOutputConfig FileExtConfig(params (string Ext, bool Discard, string Out)[] rules)
    {
        var dict = rules.ToDictionary(x => x.Ext, x => new ArchiveFileExtensionRule(x.Discard, x.Out));
        return new ArchiveOutputConfig
        {
            PlatformId = "nes", DatLineId = "nointro", StrategyType = "file_extension",
            ExtensionRules = dict,
        };
    }

    // ── 1-5 Resolver ─────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveOutputFormResolver_ReleaseShape_Chd_IsSingleFileFlat()
    {
        var releases = new[] { Rel("r1", "Sonic Adventure (USA)", "missing", File("disc.cue"), File("disc.bin")) };
        Assert.Equal(ArchiveDatLineOutputForm.SingleFileFlat,
            ArchiveDatLineOutputFormResolver.Resolve(ChdConfig(), releases));
    }

    [Fact]
    public void ArchiveOutputFormResolver_ZipOutputFile_IsSingleFileFlat()
    {
        var releases = new[] { Rel("r1", "Game Name", "missing", File("game.gba")) };
        Assert.Equal(ArchiveDatLineOutputForm.SingleFileFlat,
            ArchiveDatLineOutputFormResolver.Resolve(ZipConfig(), releases));
    }

    [Fact]
    public void ArchiveOutputFormResolver_NoCompressionFolder_IsMultiFileReleaseFolder()
    {
        var releases = new[] { Rel("r1", "Game", "missing", File("a.bin"), File("b.bin")) };
        Assert.Equal(ArchiveDatLineOutputForm.MultiFileReleaseFolder,
            ArchiveDatLineOutputFormResolver.Resolve(NoCompressionFolderConfig(), releases));
    }

    [Fact]
    public void ArchiveOutputFormResolver_FileExtension_AllSingleOutput_IsSingleFileFlat()
    {
        var config = FileExtConfig((".nes", false, ""));
        var releases = new[]
        {
            Rel("r1", "Game A", "missing", File("A.nes")),
            Rel("r2", "Game B", "missing", File("B.nes")),
        };
        Assert.Equal(ArchiveDatLineOutputForm.SingleFileFlat,
            ArchiveDatLineOutputFormResolver.Resolve(config, releases));
    }

    [Fact]
    public void ArchiveOutputFormResolver_FileExtension_AnyMultiOutput_IsMultiFileReleaseFolder()
    {
        var config = FileExtConfig((".bin", false, ".chd"));
        var releases = new[]
        {
            Rel("r1", "Single", "missing", File("only.bin")),
            Rel("r2", "Multi",  "missing", File("t1.bin"), File("t2.bin")),   // 2 outputs → Multi
        };
        Assert.Equal(ArchiveDatLineOutputForm.MultiFileReleaseFolder,
            ArchiveDatLineOutputFormResolver.Resolve(config, releases));
    }

    // ── 6-8 Planner ─────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveOutputPlan_SingleFileFlat_UsesReleaseNameBasedFilename()
    {
        var releases = new[] { Rel("r1", "Sonic Adventure (USA)", "missing", File("disc.cue"), File("disc.bin")) };
        var form = ArchiveDatLineOutputForm.SingleFileFlat;
        var plan = ArchiveOutputPlanBuilder.Build(ChdConfig(), form, releases);

        var c = plan.Single();
        Assert.Equal("Sonic Adventure (USA).chd", c.PlannedFilename);
        Assert.Equal("archive/dc/redump/Sonic Adventure (USA).chd", c.PlannedRelativePath);
    }

    [Fact]
    public void ArchiveOutputPlan_SingleFileFlat_DoesNotUseMainInputName()
    {
        var releases = new[] { Rel("r1", "Sonic Adventure (USA)", "missing", File("disc.cue"), File("disc.bin")) };
        var plan = ArchiveOutputPlanBuilder.Build(ChdConfig(), ArchiveDatLineOutputForm.SingleFileFlat, releases);

        var c = plan.Single();
        Assert.DoesNotContain("disc", c.PlannedFilename);          // not "disc.chd"
        Assert.Equal("disc.cue", c.MainInputFile);                 // main input recorded, but not the name
    }

    [Fact]
    public void ArchiveOutputPlan_MultiFileReleaseFolder_UsesReleaseFolderAndOriginalFilenames()
    {
        var releases = new[] { Rel("r1", "Game", "missing", File("Track 01.bin"), File("Track 02.bin")) };
        var plan = ArchiveOutputPlanBuilder.Build(NoCompressionFolderConfig(),
            ArchiveDatLineOutputForm.MultiFileReleaseFolder, releases);

        var c = plan.Single();
        Assert.Equal("Game", c.ArchiveEntryName);                  // release folder
        Assert.Equal("archive/psx/redump/Game", c.PlannedRelativePath);
        Assert.Contains("Track 01.bin", c.PlannedInnerFilenames);  // original filenames preserved
        Assert.Contains("Track 02.bin", c.PlannedInnerFilenames);
    }

    // ── 9-14 Collision analyzer ──────────────────────────────────────────────────

    [Fact]
    public void ArchiveOutputCollisionAnalyzer_DuplicateReleaseName_IsCollision()
    {
        var releases = new[]
        {
            Rel("r1", "Game", "missing", File("a.iso")),
            Rel("r2", "Game", "missing", File("b.iso")),
        };
        var plan = ArchiveOutputPlanBuilder.Build(ChdConfig(), ArchiveDatLineOutputForm.SingleFileFlat, releases);
        var groups = ArchiveOutputCollisionAnalyzer.Analyze(plan);

        var g = Assert.Single(groups);
        Assert.Equal("Game.chd", g.ArchiveEntryName);
        Assert.Equal(2, g.Candidates.Count);
    }

    [Fact]
    public void ArchiveOutputCollisionAnalyzer_DifferentNamesSameSafeName_IsCollision()
    {
        // "Zelda" and "  Zelda  " normalize to the same SafeReleaseName.
        var releases = new[]
        {
            Rel("r1", "Zelda",      "missing", File("a.iso")),
            Rel("r2", "  Zelda  ",  "missing", File("b.iso")),
        };
        var plan = ArchiveOutputPlanBuilder.Build(ChdConfig(), ArchiveDatLineOutputForm.SingleFileFlat, releases);
        var groups = ArchiveOutputCollisionAnalyzer.Analyze(plan);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Candidates.Count);
    }

    [Fact]
    public void ArchiveOutputCollisionAnalyzer_UnwantedRelease_IsIgnored()
    {
        var releases = new[]
        {
            Rel("r1", "Game", "missing",  File("a.iso")),
            Rel("r2", "Game", "unwanted", File("b.iso")),   // excluded from analysis
        };
        var plan = ArchiveOutputPlanBuilder.Build(ChdConfig(), ArchiveDatLineOutputForm.SingleFileFlat, releases);
        var groups = ArchiveOutputCollisionAnalyzer.Analyze(plan);

        Assert.Empty(groups);   // only one wanted release maps to "Game.chd"
    }

    [Fact]
    public void ArchiveOutputCollisionAnalyzer_MultiFileReleaseFolder_CommonTrackNames_NotCollision()
    {
        // Different release names → different folders; common inner track names must NOT collide.
        var releases = new[]
        {
            Rel("r1", "Alpha", "missing", File("track01.bin"), File("track02.bin")),
            Rel("r2", "Beta",  "missing", File("track01.bin"), File("track02.bin")),
        };
        var plan = ArchiveOutputPlanBuilder.Build(NoCompressionFolderConfig(),
            ArchiveDatLineOutputForm.MultiFileReleaseFolder, releases);
        var groups = ArchiveOutputCollisionAnalyzer.Analyze(plan);

        Assert.Empty(groups);
    }

    [Fact]
    public void ArchiveOutputCollisionAnalyzer_ThreeWayCollision_ReturnsSingleGroupWithThreeCandidates()
    {
        var releases = new[]
        {
            Rel("r1", "Game", "missing", File("a.iso")),
            Rel("r2", "Game", "missing", File("b.iso")),
            Rel("r3", "Game", "missing", File("c.iso")),
        };
        var plan = ArchiveOutputPlanBuilder.Build(ChdConfig(), ArchiveDatLineOutputForm.SingleFileFlat, releases);
        var groups = ArchiveOutputCollisionAnalyzer.Analyze(plan);

        var g = Assert.Single(groups);
        Assert.Equal(3, g.Candidates.Count);
    }

    [Fact]
    public void ArchiveOutputCollisionCandidate_ContainsSourceFilesAndHashes_WhenAvailable()
    {
        var releases = new[]
        {
            Rel("r1", "Sonic Adventure (USA)", "missing",
                File("disc.cue", sha1: "aabbccdd", size: "10"),
                File("disc.bin", sha1: "11223344", size: "2048")),
        };
        var plan = ArchiveOutputPlanBuilder.Build(ChdConfig(), ArchiveDatLineOutputForm.SingleFileFlat, releases);

        var c = plan.Single();
        Assert.Equal(2, c.SourceFiles.Count);
        Assert.Contains(c.SourceFiles, sf => sf.RomName == "disc.cue" && sf.Sha1 == "aabbccdd" && sf.SizeBytes == 10);
        Assert.Contains(c.SourceFiles, sf => sf.RomName == "disc.bin" && sf.SizeBytes == 2048);
        Assert.Equal(2058, c.TotalSourceBytes);
        Assert.Equal("release:r1", c.ContentIdentityKey);
    }

    // ── Validation: structural vs exclusion-sensitive ────────────────────────────

    // 1
    [Fact]
    public void ArchiveOutputValidation_FullSetNoCollision_ValidFullSet()
    {
        var releases = new[]
        {
            Rel("r1", "Game A", "missing", File("a.iso")),
            Rel("r2", "Game B", "missing", File("b.iso")),
        };
        var result = ArchiveOutputValidator.Validate(ChdConfig(), releases);

        Assert.False(result.FullSetHasCollision);
        Assert.Equal(ArchiveDatLineOutputForm.SingleFileFlat, result.Form);
        Assert.Equal(ArchiveOutputValidationState.ValidFullSet, result.State);
    }

    // 2
    [Fact]
    public void ArchiveOutputValidation_FullSetNoCollision_ExcludeDoesNotMakeStale()
    {
        var full = new[]
        {
            Rel("r1", "Game A", "missing", File("a.iso")),
            Rel("r2", "Game B", "missing", File("b.iso")),
        };
        var validated = ArchiveOutputValidator.Validate(ChdConfig(), full);
        var stored = validated.StructuralFingerprint;

        // Curator excludes one release afterwards.
        var afterExclude = new[]
        {
            Rel("r1", "Game A", "missing",  File("a.iso")),
            Rel("r2", "Game B", "unwanted", File("b.iso")),
        };
        var current = ArchiveOutputValidator.Validate(ChdConfig(), afterExclude);

        // Structural fingerprint unchanged → not stale; still ValidFullSet.
        Assert.Equal(stored, current.StructuralFingerprint);
        Assert.Equal(ArchiveOutputValidationState.ValidFullSet,
            ArchiveOutputValidator.ComputeState(current, stored));
    }

    // 3
    [Fact]
    public void ArchiveOutputValidation_FullSetNoCollision_RestoreDoesNotMakeStale()
    {
        var full = new[]
        {
            Rel("r1", "Game A", "missing",  File("a.iso")),
            Rel("r2", "Game B", "unwanted", File("b.iso")),
        };
        var validated = ArchiveOutputValidator.Validate(ChdConfig(), full);
        var stored = validated.StructuralFingerprint;

        // Restore the excluded release.
        var afterRestore = new[]
        {
            Rel("r1", "Game A", "missing", File("a.iso")),
            Rel("r2", "Game B", "missing", File("b.iso")),
        };
        var current = ArchiveOutputValidator.Validate(ChdConfig(), afterRestore);

        Assert.Equal(stored, current.StructuralFingerprint);
        Assert.Equal(ArchiveOutputValidationState.ValidFullSet,
            ArchiveOutputValidator.ComputeState(current, stored));
    }

    // 4
    [Fact]
    public void ArchiveOutputValidation_FullSetCollision_CurrentWantedStillCollides_CollisionUnresolved()
    {
        var releases = new[]
        {
            Rel("r1", "Game", "missing", File("a.iso")),
            Rel("r2", "Game", "missing", File("b.iso")),
        };
        var result = ArchiveOutputValidator.Validate(ChdConfig(), releases);

        Assert.True(result.FullSetHasCollision);
        Assert.True(result.WantedSubsetHasCollision);
        Assert.Equal(ArchiveOutputValidationState.CollisionUnresolved, result.State);
    }

    // 5
    [Fact]
    public void ArchiveOutputValidation_FullSetCollision_CurrentWantedResolvedByExclusion_ValidWithExclusions()
    {
        var releases = new[]
        {
            Rel("r1", "Game", "missing",  File("a.iso")),
            Rel("r2", "Game", "unwanted", File("b.iso")),   // resolves the collision
        };
        var result = ArchiveOutputValidator.Validate(ChdConfig(), releases);

        Assert.True(result.FullSetHasCollision);
        Assert.False(result.WantedSubsetHasCollision);
        Assert.Equal(ArchiveOutputValidationState.ValidWithExclusions, result.State);
    }

    // 6
    [Fact]
    public void ArchiveOutputValidation_ValidWithExclusions_RestoreExcludedRelease_ReintroducesCollision()
    {
        var resolved = new[]
        {
            Rel("r1", "Game", "missing",  File("a.iso")),
            Rel("r2", "Game", "unwanted", File("b.iso")),
        };
        var validated = ArchiveOutputValidator.Validate(ChdConfig(), resolved);
        Assert.Equal(ArchiveOutputValidationState.ValidWithExclusions, validated.State);
        var stored = validated.StructuralFingerprint;

        // Restore r2 → the wanted subset collides again.
        var restored = new[]
        {
            Rel("r1", "Game", "missing", File("a.iso")),
            Rel("r2", "Game", "missing", File("b.iso")),
        };
        var current = ArchiveOutputValidator.Validate(ChdConfig(), restored);

        // Structural fingerprint unchanged (curation only) → not Stale, but CollisionUnresolved.
        Assert.Equal(stored, current.StructuralFingerprint);
        Assert.Equal(ArchiveOutputValidationState.CollisionUnresolved,
            ArchiveOutputValidator.ComputeState(current, stored));
    }

    // 7
    [Fact]
    public void ArchiveOutputValidation_StrategyChange_MakesStructuralStale()
    {
        var releases = new[] { Rel("r1", "Game", "missing", File("game.gba")) };

        var underChd = ArchiveOutputValidator.Validate(ChdConfig(), releases);
        var underZip = ArchiveOutputValidator.Validate(ZipConfig(), releases);
        Assert.NotEqual(underChd.StructuralFingerprint, underZip.StructuralFingerprint);

        // A validation stored under CHD is stale once the strategy is ZIP.
        Assert.Equal(ArchiveOutputValidationState.Stale,
            ArchiveOutputValidator.ComputeState(underZip, underChd.StructuralFingerprint));
    }

    // 8
    [Fact]
    public void ArchiveOutputValidation_DatNameChange_MakesStructuralStale()
    {
        var v1 = new[] { Rel("r1", "Game", "missing", File("disc.cue"), File("disc.bin")) };
        var v2 = new[] { Rel("r1", "Game (Rev 1)", "missing", File("disc.cue"), File("disc.bin")) };

        var before = ArchiveOutputValidator.Validate(ChdConfig(), v1);
        var after  = ArchiveOutputValidator.Validate(ChdConfig(), v2);
        Assert.NotEqual(before.StructuralFingerprint, after.StructuralFingerprint);

        Assert.Equal(ArchiveOutputValidationState.Stale,
            ArchiveOutputValidator.ComputeState(after, before.StructuralFingerprint));
    }

    // 9
    [Fact]
    public void ArchiveOutputValidation_StructuralFingerprint_DoesNotIncludeWantedStatus_ForValidFullSet()
    {
        var allWanted = new[]
        {
            Rel("r1", "Game A", "missing", File("a.iso")),
            Rel("r2", "Game B", "missing", File("b.iso")),
        };
        var oneUnwanted = new[]
        {
            Rel("r1", "Game A", "missing",  File("a.iso")),
            Rel("r2", "Game B", "unwanted", File("b.iso")),
        };

        var fpAllWanted   = ArchiveOutputFingerprint.ComputeStructural(
            ChdConfig(), ArchiveDatLineOutputForm.SingleFileFlat, allWanted);
        var fpOneUnwanted = ArchiveOutputFingerprint.ComputeStructural(
            ChdConfig(), ArchiveDatLineOutputForm.SingleFileFlat, oneUnwanted);

        Assert.Equal(fpAllWanted, fpOneUnwanted);   // status does not affect the structural fingerprint
    }

    // 10
    [Fact]
    public void ArchiveOutputValidation_ExclusionSensitiveFingerprint_OnlyUsedForValidWithExclusions()
    {
        var allWanted = new[]
        {
            Rel("r1", "Game", "missing", File("a.iso")),
            Rel("r2", "Other", "missing", File("b.iso")),
        };
        var oneExcluded = new[]
        {
            Rel("r1", "Game", "missing",  File("a.iso")),
            Rel("r2", "Other", "unwanted", File("b.iso")),
        };

        // Exclusion fingerprint tracks the unwanted set (changes when exclusions change)…
        Assert.NotEqual(
            ArchiveOutputFingerprint.ComputeExclusion(allWanted),
            ArchiveOutputFingerprint.ComputeExclusion(oneExcluded));

        // …but a ValidFullSet line's state/staleness never depends on it — only the
        // structural fingerprint gates it, so excluding keeps it ValidFullSet.
        var validated = ArchiveOutputValidator.Validate(ChdConfig(), allWanted);
        var current   = ArchiveOutputValidator.Validate(ChdConfig(), oneExcluded);
        Assert.Equal(ArchiveOutputValidationState.ValidFullSet, validated.State);
        Assert.Equal(ArchiveOutputValidationState.ValidFullSet,
            ArchiveOutputValidator.ComputeState(current, validated.StructuralFingerprint));
    }

    // ── Unknown form for legacy / "none" strategy ────────────────────────────────

    [Fact]
    public void ArchiveOutputFormResolver_NoneStrategy_IsUnknown()
    {
        var config = new ArchiveOutputConfig { PlatformId = "p", DatLineId = "d", StrategyType = "none" };
        Assert.Equal(ArchiveDatLineOutputForm.Unknown,
            ArchiveDatLineOutputFormResolver.Resolve(config, System.Array.Empty<ArchiveReleaseInput>()));
    }
}
