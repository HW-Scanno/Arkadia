using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// RVZ-SUPPORT-01 wiring that cannot be constructed headless (MainWindow): Deep Hashing is selective (only .rvz,
/// only an ordinary Single run — never Repair, never Group), sits after archive extraction and before the ordinary
/// matcher, and Phase A routes ONLY rvz_compression through the verified conversion. Everything runnable without a
/// window is covered functionally in RvzDeepHashingTests / RvzConversionTests.
/// </summary>
public sealed class RvzIngestionWiringTests
{
    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    private static string Read(string rel) => File.ReadAllText(RepoFile(rel)).Replace("\r\n", "\n");

    private static string Region(string src, string start, string end)
    {
        var s = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(s >= 0, start);
        var e = src.IndexOf(end, s + start.Length, StringComparison.Ordinal);
        Assert.True(e > s, end);
        return src.Substring(s, e - s);
    }

    private static string Worker() => Region(Read("MainWindow.axaml.cs"),
        "private IngestionResult RunIngestionWork(", "// Called from the outer release loop in RunIngestionWork.");

    [Fact]
    public void DeepHashing_RunsAfterExtraction_BeforeOrdinaryMatching()
    {
        var w = Worker();
        int extract = w.IndexOf("RunPreIngest(incomingDir", StringComparison.Ordinal);
        int deep    = w.IndexOf("RunRvzDeepHashing(", StringComparison.Ordinal);
        int load    = w.IndexOf("var allReleasesList = store.LoadReleases();", StringComparison.Ordinal);
        int match   = w.IndexOf("IngestionCandidateMatcher.HashAndMatch(", StringComparison.Ordinal);
        Assert.True(extract >= 0 && deep > extract && load > deep && match > load);
    }

    [Fact]
    public void DeepHashing_IsGated_ToOrdinarySingleRuns_AndRvzCandidatesOnly()
    {
        var w = Worker();
        var gate = Region(w, "Ingestion.RvzDeepHashOutcome? deepHash = null;", "var store = new DatLineStore(absDbPath);");
        Assert.Contains("if (shouldIngest is null && incomingDirOverride is null)", gate);       // never Repair
        Assert.Contains("sourceFiles.Where(Ingestion.RvzDeepHashing.IsCandidate)", gate);       // only .rvz
        Assert.Contains("if (rvzCandidates.Count > 0)", gate);                                  // zero cost otherwise
        Assert.Contains("sourceFiles.RemoveAll(deepHash.Consumed.Contains)", gate);             // never re-processed
    }

    [Fact]
    public void GroupIngestion_DoesNotUseDeepHashing()
    {
        var root = RepoFile("");
        var groupSources = Directory.GetFiles(Path.Combine(root, "Ingestion"), "Group*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "GroupDats"), "*.cs", SearchOption.AllDirectories));
        foreach (var f in groupSources)
            Assert.DoesNotContain("RvzDeepHashing", File.ReadAllText(f));

        var mw = Read("MainWindow.axaml.cs");
        Assert.Equal(2, CountOccurrences(mw, "RunRvzDeepHashing("));   // definition + the single call in RunIngestionWork
    }

    [Fact]
    public void PhaseA_OnlyRvzTransform_UsesVerifiedConversion_OthersUnchanged()
    {
        var p = Region(Read("MainWindow.axaml.cs"), "private void ProcessFileOrientedRelease(", "// ── Configured leaf finalization adapter");
        Assert.Contains("bool isRvz       = Ingestion.RvzIds.IsRvzTransform(effectiveXform);", p);
        Assert.Contains("Ingestion.RvzConversion.Run(", p);
        // Non-RVZ: historical direct transform, with the archive parent created lazily just before it writes.
        Assert.Contains("Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);\n                        if (!TransformEngine.ExecuteTransform(effectiveXform, effectiveTool, appRoot, sourceFilePath, destPath, out var xformError))", p);
        Assert.Contains("!isRvz      ? effectiveXform.ArchiveTier", p);                         // non-RVZ tier untouched
        Assert.Contains("rvzVerified ? \"A\"", p);                                              // Tier A only when verified
    }

    [Fact]
    public void OtherProcessors_DoNotReferenceRvz()
    {
        var mw = Read("MainWindow.axaml.cs");
        foreach (var (start, end) in new[]
                 {
                     ("private void ProcessFolderOrientedRelease(",       "private void ProcessReleaseShapeOrientedRelease("),
                     ("private void ProcessReleaseShapeOrientedRelease(", "private void ProcessFolderOrientedFolderRelease("),
                 })
            Assert.DoesNotContain("Rvz", Region(mw, start, end));
    }

    private static int CountOccurrences(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void SourceVerified_AddsOnlyTheNarrowRvzRule_GenericRuleUnchanged_AllCallSitesPassDerived()
    {
        var mw = Read("MainWindow.axaml.cs");
        var fn = Region(mw, "private static bool IsSourceVerified(", "private static Control MakeSourceFileRow(");

        // The RVZ rule is consulted first; the pre-existing generic rule follows verbatim.
        int rvz  = fn.IndexOf("Ingestion.RvzIds.IsVerifiedLogicalSourceWithoutMd5(f, src, derived)", StringComparison.Ordinal);
        int sha1 = fn.IndexOf("if (f.Sha1.Length > 0) { anyDat = true; if (!string.Equals(src.HashedSourceSha1,", StringComparison.Ordinal);
        Assert.True(rvz > 0 && sha1 > rvz);
        Assert.Contains("if (f.Md5.Length  > 0) { anyDat = true; if (!string.Equals(src.HashedSourceMd5  ?? \"\",   f.Md5,", fn);
        Assert.Contains("if (f.Crc.Length  > 0) { anyDat = true; if (!string.Equals(src.HashedSourceCrc32 ?? \"\",  f.Crc,", fn);
        Assert.Contains("return anyDat;", fn);

        // Every caller hands over the derived artifact linked to the row (no 2-argument call left).
        Assert.Equal(5, CountOccurrences(mw, "IsSourceVerified("));    // definition + 4 call sites
        Assert.Contains("IsSourceVerified(t.F, t.Src, t.Dst)", mw);
        Assert.Contains("IsSourceVerified(f, src, dst)", mw);
        Assert.Equal(2, CountOccurrences(mw, "IsSourceVerified(f, source, derived)"));
        Assert.Equal(2, CountOccurrences(mw, "MakeSourceFileRow(f, src, dst, extTransforms, CopyAndToast)"));
    }
}
