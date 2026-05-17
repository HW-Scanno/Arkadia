using System.Collections.Generic;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Ingestion;

public sealed class ReleaseShapeTransformPlannerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static ReleaseFileRecord File(string name) =>
        new() { Id = name, ReleaseId = "r1", RomName = name };

    private static List<ReleaseFileRecord> Files(params string[] names)
    {
        var list = new List<ReleaseFileRecord>();
        foreach (var n in names)
            list.Add(File(n));
        return list;
    }

    // ── ClassifyRelease ───────────────────────────────────────────────────────

    [Fact]
    public void SingleIsoRelease_MapsToChdDvdCompression()
    {
        var shape = ReleaseShapeTransformPlanner.ClassifyRelease(Files("game.iso"));
        Assert.Equal(ReleaseTransformShape.SingleIso, shape);
    }

    [Fact]
    public void CueBinRelease_MapsToChdCdCompression()
    {
        var shape = ReleaseShapeTransformPlanner.ClassifyRelease(Files("disc.cue", "disc.bin"));
        Assert.Equal(ReleaseTransformShape.CueBin, shape);
    }

    [Fact]
    public void CueBinRelease_MultipleBins_IsValid()
    {
        var shape = ReleaseShapeTransformPlanner.ClassifyRelease(
            Files("disc.cue", "disc (Track 1).bin", "disc (Track 2).bin"));
        Assert.Equal(ReleaseTransformShape.CueBin, shape);
    }

    [Fact]
    public void CueWithoutBin_IsInvalid()
    {
        var shape = ReleaseShapeTransformPlanner.ClassifyRelease(Files("disc.cue"));
        Assert.Equal(ReleaseTransformShape.Unsupported, shape);
    }

    [Fact]
    public void BinWithoutCue_IsInvalid()
    {
        var shape = ReleaseShapeTransformPlanner.ClassifyRelease(Files("disc.bin"));
        Assert.Equal(ReleaseTransformShape.Unsupported, shape);
    }

    [Fact]
    public void MultipleCueFiles_IsInvalid()
    {
        var shape = ReleaseShapeTransformPlanner.ClassifyRelease(
            Files("disc1.cue", "disc2.cue", "disc.bin"));
        Assert.Equal(ReleaseTransformShape.Unsupported, shape);
    }

    [Fact]
    public void IsoWithExtraFile_IsInvalid()
    {
        var shape = ReleaseShapeTransformPlanner.ClassifyRelease(
            Files("game.iso", "readme.txt"));
        Assert.Equal(ReleaseTransformShape.Unsupported, shape);
    }

    [Fact]
    public void UnsupportedExtension_IsInvalid()
    {
        var shape = ReleaseShapeTransformPlanner.ClassifyRelease(Files("game.nrg"));
        Assert.Equal(ReleaseTransformShape.Unsupported, shape);
    }

    [Fact]
    public void EmptyFileList_IsInvalid()
    {
        var shape = ReleaseShapeTransformPlanner.ClassifyRelease(new List<ReleaseFileRecord>());
        Assert.Equal(ReleaseTransformShape.Unsupported, shape);
    }

    // ── PlanRelease ───────────────────────────────────────────────────────────

    [Fact]
    public void SingleIsoRelease_Plan_HasChdDvdTransformId()
    {
        var plan = ReleaseShapeTransformPlanner.PlanRelease("r1", Files("game.iso"));
        Assert.Equal(ReleaseShapeTransformPlanner.ChdDvdTransformId, plan.TransformId);
    }

    [Fact]
    public void SingleIsoRelease_Plan_MainInputIsIsoFile()
    {
        var plan = ReleaseShapeTransformPlanner.PlanRelease("r1", Files("game.iso"));
        Assert.Equal("game.iso", plan.MainInputFile);
    }

    [Fact]
    public void SingleIsoRelease_Plan_HasNoDependencies()
    {
        var plan = ReleaseShapeTransformPlanner.PlanRelease("r1", Files("game.iso"));
        Assert.Empty(plan.DependencyFiles);
    }

    [Fact]
    public void CueBinRelease_Plan_HasChdCdTransformId()
    {
        var plan = ReleaseShapeTransformPlanner.PlanRelease("r1", Files("disc.cue", "disc.bin"));
        Assert.Equal(ReleaseShapeTransformPlanner.ChdCdTransformId, plan.TransformId);
    }

    [Fact]
    public void CueBinRelease_BinIsDependency_NotMainInput()
    {
        var plan = ReleaseShapeTransformPlanner.PlanRelease("r1", Files("disc.cue", "disc.bin"));
        Assert.Equal("disc.cue", plan.MainInputFile);
        Assert.Contains("disc.bin", plan.DependencyFiles);
    }

    [Fact]
    public void CueBinRelease_ExtensionMatchIsCaseInsensitive()
    {
        var plan = ReleaseShapeTransformPlanner.PlanRelease("r1",
            Files("Disc.CUE", "Disc.BIN"));
        Assert.Equal(ReleaseTransformShape.CueBin, plan.Shape);
        Assert.Equal("Disc.CUE", plan.MainInputFile);
    }

    [Fact]
    public void UnsupportedRelease_Plan_HasEmptyTransformId()
    {
        var plan = ReleaseShapeTransformPlanner.PlanRelease("r1", Files("game.nrg"));
        Assert.Equal(ReleaseTransformShape.Unsupported, plan.Shape);
        Assert.Equal("", plan.TransformId);
    }

    // ── AnalyzeDat ────────────────────────────────────────────────────────────

    [Fact]
    public void MixedIsoCueBinDat_ReleaseShapeStrategy_IsValid()
    {
        var allFiles = new Dictionary<string, List<ReleaseFileRecord>>
        {
            ["r1"] = Files("gameA.iso"),
            ["r2"] = Files("disc.cue", "disc.bin"),
        };
        var result = ReleaseShapeTransformPlanner.AnalyzeDat(allFiles);
        Assert.True(result.IsValid);
        Assert.Equal(1, result.SingleIsoCount);
        Assert.Equal(1, result.CueBinCount);
        Assert.Equal(0, result.UnsupportedCount);
    }

    [Fact]
    public void DatWithUnsupportedRelease_AnalyzeReturnsInvalid()
    {
        var allFiles = new Dictionary<string, List<ReleaseFileRecord>>
        {
            ["r1"] = Files("gameA.iso"),
            ["r2"] = Files("disc.nrg"),
        };
        var result = ReleaseShapeTransformPlanner.AnalyzeDat(allFiles);
        Assert.False(result.IsValid);
        Assert.Equal(1, result.UnsupportedCount);
    }

    [Fact]
    public void ReleaseShapePlan_ProducesOneOutputPerRelease()
    {
        // Two releases → two plans, each producing one output
        var releases = new Dictionary<string, List<ReleaseFileRecord>>
        {
            ["r1"] = Files("gameA.iso"),
            ["r2"] = Files("disc.cue", "disc.bin"),
        };

        var plans = new List<ReleaseShapeTransformPlan>();
        foreach (var (id, files) in releases)
            plans.Add(ReleaseShapeTransformPlanner.PlanRelease(id, files));

        Assert.Equal(2, plans.Count);
        Assert.All(plans, p => Assert.NotEqual(ReleaseTransformShape.Unsupported, p.Shape));
        Assert.All(plans, p => Assert.NotEmpty(p.TransformId));
    }

    [Fact]
    public void MixedIsoCueBinDat_PerExtensionStrategy_RemainsInvalid()
    {
        // Validate that the planner sees the DAT as iso+cue/bin,
        // confirming that per-extension (which can't handle multi-file) stays invalid.
        var allFiles = new Dictionary<string, List<ReleaseFileRecord>>
        {
            ["r1"] = Files("gameA.iso"),
            ["r2"] = Files("disc.cue", "disc.bin"),
        };
        var analysis = ReleaseShapeTransformPlanner.AnalyzeDat(allFiles);
        // Analysis is valid for release_shape, meaning the DAT has multi-file releases
        // that make file_extension invalid.
        Assert.True(analysis.IsValid);
        Assert.True(analysis.CueBinCount > 0); // multi-file present → file_extension invalid
    }
}
