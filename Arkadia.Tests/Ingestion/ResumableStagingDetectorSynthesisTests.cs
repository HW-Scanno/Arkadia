using System;
using System.IO;
using System.Linq;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Synthesis-aware resumable detection: authoritative empty requirements may stand in for staged files ONLY for a
/// <c>missing</c> release; historical behavior is unchanged when no synthesizable files are supplied.
/// </summary>
public sealed class ResumableStagingDetectorSynthesisTests : IDisposable
{
    private const string Name = "Store Operating System Initialization v3.5 (1982)(Tandy) [CMD]";

    private readonly string _root;
    private readonly string _stagingRoot;

    public ResumableStagingDetectorSynthesisTests()
    {
        _root        = Path.Combine(Path.GetTempPath(), "ArkResumeSynth_" + Guid.NewGuid().ToString("N")[..8]);
        _stagingRoot = Path.Combine(_root, "staging", "trs80", "leaf");
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private void Stage(params string[] files)
    {
        var folder = Path.Combine(_stagingRoot, IngestionPaths.SafeFolderName(Name));
        Directory.CreateDirectory(folder);
        foreach (var f in files) File.WriteAllBytes(Path.Combine(folder, f), new byte[] { 1 });
    }

    private ResumableStagingResult Detect(string status, string[] expected, string[]? synthesizable)
        => ResumableStagingDetector.Detect(_stagingRoot,
            new[] { new ResumeReleaseInput("r1", Name, status, expected.ToList(), synthesizable) });

    [Fact]
    public void Missing_AllSynthesizable_NoStagingFolder_IsResumable()
        => Assert.Contains("r1", Detect("missing", new[] { "STORE.DAT" }, new[] { "STORE.DAT" }).ResumableReleaseIds);

    [Fact]  // the stuck TRS-80 case
    public void Missing_HashedStaged_EmptyAbsent_IsResumable()
    {
        Stage("STRINT.CMD");
        Assert.Contains("r1", Detect("missing", new[] { "STRINT.CMD", "STORE.DAT" }, new[] { "STORE.DAT" }).ResumableReleaseIds);
    }

    [Fact]
    public void Missing_HashedNotStaged_IsNotResumable()
    {
        var r = Detect("missing", new[] { "STRINT.CMD", "STORE.DAT" }, new[] { "STORE.DAT" });
        Assert.DoesNotContain("r1", r.ResumableReleaseIds);
        Assert.Contains(r.Skipped, s => s.ReleaseId == "r1" && s.Reason == "no-staging");
    }

    [Fact]
    public void Missing_HashedNotStaged_FolderExists_IsIncomplete()
    {
        Stage("OTHER.TXT");
        var r = Detect("missing", new[] { "STRINT.CMD", "STORE.DAT" }, new[] { "STORE.DAT" });
        Assert.Contains(r.Skipped, s => s.ReleaseId == "r1" && s.Reason == "incomplete-staging");
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("pending")]
    public void NonMissing_SynthesisNeverStandsInForStaging(string status)
    {
        Assert.DoesNotContain("r1", Detect(status, new[] { "STORE.DAT" }, new[] { "STORE.DAT" }).ResumableReleaseIds);

        Stage("STRINT.CMD");
        Assert.DoesNotContain("r1", Detect(status, new[] { "STRINT.CMD", "STORE.DAT" }, new[] { "STORE.DAT" }).ResumableReleaseIds);
    }

    [Theory]
    [InlineData("unwanted")]
    [InlineData("present")]
    public void UnwantedOrPresent_NeverResumable(string status)
        => Assert.DoesNotContain("r1", Detect(status, new[] { "STORE.DAT" }, new[] { "STORE.DAT" }).ResumableReleaseIds);

    [Fact]  // historical behavior: without synthesizable files an absent file keeps the release non-resumable
    public void NoSynthesizableFiles_HistoricalBehaviorPreserved()
    {
        Stage("STRINT.CMD");
        Assert.DoesNotContain("r1", Detect("missing", new[] { "STRINT.CMD", "STORE.DAT" }, null).ResumableReleaseIds);
        Assert.Contains("r1", Detect("missing", new[] { "STRINT.CMD" }, null).ResumableReleaseIds);
    }
}
