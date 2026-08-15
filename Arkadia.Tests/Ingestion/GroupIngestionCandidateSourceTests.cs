using System;
using System.IO;
using System.Linq;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Candidate enumeration + container classification for the Group runtime bridge (M9C2B). Temp incoming trees
/// only. Enforces the preservation rule: in <c>archives_pre_extraction</c> every container-extension file is
/// excluded from candidates (successful, failed/skipped, or nested), so an uninspected archive is never hashed
/// into a false zero-match.
/// </summary>
public sealed class GroupIngestionCandidateSourceTests : IDisposable
{
    private readonly string _incoming;

    public GroupIngestionCandidateSourceTests()
    {
        _incoming = Path.Combine(Path.GetTempPath(), "ark-cand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_incoming);
    }

    public void Dispose() { try { Directory.Delete(_incoming, true); } catch { } }

    private string Write(string rel, string content = "x")
    {
        var p = Path.Combine(_incoming, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact] // 9 + 10 + 14: pre_extraction excludes the container, includes the loose member; all_files includes both
    public void PreExtraction_ExcludesContainers_IncludesMembers()
    {
        Write("game.zip");
        var member = Write("game/rom.d64");
        var loose  = Write("plain.d64");

        var pre = GroupIngestionCandidateSource.EnumerateCandidates(_incoming, "archives_pre_extraction");
        Assert.Contains(member, pre);
        Assert.Contains(loose, pre);
        Assert.DoesNotContain(pre, p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        var all = GroupIngestionCandidateSource.EnumerateCandidates(_incoming, "all_files");
        Assert.Contains(Path.Combine(_incoming, "game.zip"), all);   // .zip is an ordinary candidate
    }

    [Fact] // 13: a nested container inside an extraction folder is excluded from this run's candidates and preserved
    public void PreExtraction_ExcludesNestedContainer_ButFileRemains()
    {
        var nested = Write("outer/inner.7z");
        var member = Write("outer/rom.bin");

        var pre = GroupIngestionCandidateSource.EnumerateCandidates(_incoming, "archives_pre_extraction");

        Assert.DoesNotContain(nested, pre);
        Assert.Contains(member, pre);
        Assert.True(File.Exists(nested));   // still on disk for a later run
    }

    [Fact] // container extension set is .zip/.7z/.rar, case-insensitive
    public void SnapshotContainers_FindsAllThreeExtensions_CaseInsensitive()
    {
        Write("a.zip"); Write("b.7Z"); Write("c.RAR"); Write("d.d64"); Write("e.txt");

        var snap = GroupIngestionCandidateSource.SnapshotContainers(_incoming);

        Assert.Equal(3, snap.Count);
        Assert.All(snap, p => Assert.True(ArchiveContainerExtensions.IsContainer(p)));
    }

    [Fact] // 11 + 12 (classification half): attempted − successful = failed/skipped (kept, never a candidate)
    public void ClassifyContainers_SplitsSuccessfulFromFailed()
    {
        var ok   = Write("ok.zip");
        var bad  = Write("bad.7z");
        var attempted = GroupIngestionCandidateSource.SnapshotContainers(_incoming);

        var extracted = new[]
        {
            new ExtractedArchiveInfo(ok, Path.Combine(_incoming, "ok"), Array.Empty<string>()),
        };

        var (successful, failed) = GroupIngestionCandidateSource.ClassifyContainers(attempted, extracted);

        Assert.Equal(new[] { ok }, successful);
        Assert.Equal(new[] { bad }, failed);
        // Neither is ever a candidate in pre_extraction mode:
        var cand = GroupIngestionCandidateSource.EnumerateCandidates(_incoming, "archives_pre_extraction");
        Assert.DoesNotContain(ok, cand);
        Assert.DoesNotContain(bad, cand);
        Assert.True(File.Exists(bad));   // failed container remains in incoming
    }

    [Fact]
    public void EnumerateCandidates_DeterministicOrdinalOrder()
    {
        Write("z.d64"); Write("a.d64"); Write("m.d64");
        var cand = GroupIngestionCandidateSource.EnumerateCandidates(_incoming, "archives_pre_extraction");
        Assert.Equal(cand.OrderBy(p => p, StringComparer.Ordinal).ToList(), cand);
    }
}
