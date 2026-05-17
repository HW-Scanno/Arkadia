using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;

namespace Arkadia;

public enum ReleaseTransformShape
{
    Unsupported,
    SingleIso,
    CueBin
}

public sealed record ReleaseShapeAnalysisResult(
    bool IsValid,
    int SingleIsoCount,
    int CueBinCount,
    int UnsupportedCount,
    IReadOnlyList<string> UnsupportedExamples);

public sealed record ReleaseShapeTransformPlan(
    string ReleaseId,
    ReleaseTransformShape Shape,
    string TransformId,
    string MainInputFile,
    IReadOnlyList<string> DependencyFiles);

public static class ReleaseShapeTransformPlanner
{
    public const string ChdDvdTransformId = "chd_dvd_compression";
    public const string ChdCdTransformId  = "chd_cd_compression";

    public static ReleaseTransformShape ClassifyRelease(IReadOnlyList<ReleaseFileRecord> files)
    {
        if (files.Count == 0)
            return ReleaseTransformShape.Unsupported;

        var exts = files
            .Select(f => Path.GetExtension(f.RomName).ToLowerInvariant())
            .ToList();

        if (files.Count == 1 && exts[0] == ".iso")
            return ReleaseTransformShape.SingleIso;

        var cueCount   = exts.Count(e => e == ".cue");
        var binCount   = exts.Count(e => e == ".bin");
        var otherCount = exts.Count(e => e != ".cue" && e != ".bin");

        if (cueCount == 1 && binCount >= 1 && otherCount == 0)
            return ReleaseTransformShape.CueBin;

        return ReleaseTransformShape.Unsupported;
    }

    public static ReleaseShapeTransformPlan PlanRelease(
        string releaseId,
        IReadOnlyList<ReleaseFileRecord> files)
    {
        var shape = ClassifyRelease(files);

        switch (shape)
        {
            case ReleaseTransformShape.SingleIso:
                return new ReleaseShapeTransformPlan(
                    releaseId, shape, ChdDvdTransformId,
                    files[0].RomName,
                    Array.Empty<string>());

            case ReleaseTransformShape.CueBin:
                var mainInput = files
                    .First(f => Path.GetExtension(f.RomName)
                        .Equals(".cue", StringComparison.OrdinalIgnoreCase))
                    .RomName;
                var deps = files
                    .Where(f => Path.GetExtension(f.RomName)
                        .Equals(".bin", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.RomName)
                    .ToArray();
                return new ReleaseShapeTransformPlan(
                    releaseId, shape, ChdCdTransformId,
                    mainInput, deps);

            default:
                return new ReleaseShapeTransformPlan(
                    releaseId, ReleaseTransformShape.Unsupported, "",
                    "", Array.Empty<string>());
        }
    }

    public static ReleaseShapeAnalysisResult AnalyzeDat(
        Dictionary<string, List<ReleaseFileRecord>> allReleaseFiles)
    {
        var singleIso   = 0;
        var cueBin      = 0;
        var unsupported = 0;
        var examples    = new List<string>();

        foreach (var (releaseId, files) in allReleaseFiles)
        {
            var shape = ClassifyRelease(files);
            switch (shape)
            {
                case ReleaseTransformShape.SingleIso: singleIso++;  break;
                case ReleaseTransformShape.CueBin:    cueBin++;     break;
                default:
                    unsupported++;
                    if (examples.Count < 3)
                        examples.Add(releaseId);
                    break;
            }
        }

        var isValid = unsupported == 0 && (singleIso + cueBin) > 0;
        return new ReleaseShapeAnalysisResult(
            isValid, singleIso, cueBin, unsupported, examples);
    }
}
