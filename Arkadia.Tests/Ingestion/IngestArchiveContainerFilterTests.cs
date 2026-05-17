using System.IO;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

public sealed class IngestArchiveContainerFilterTests
{
    private static string Abs(string relative) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), relative));

    // Wrap a path string into a minimal ExtractedArchiveInfo for filter tests.
    private static ExtractedArchiveInfo Info(string archivePath) =>
        new(archivePath, archivePath + "_root", []);

    // ── BuildExtractedSet ─────────────────────────────────────────────────────

    [Fact]
    public void BuildExtractedSet_EmptyInput_ReturnsEmptySet()
    {
        var set = IngestArchiveContainerFilter.BuildExtractedSet([]);
        Assert.Empty(set);
    }

    [Fact]
    public void BuildExtractedSet_NormalizesToFullPath()
    {
        var path = Abs("game.zip");
        var set  = IngestArchiveContainerFilter.BuildExtractedSet([Info(path)]);
        Assert.Contains(path, set);
    }

    // ── IsExtractedArchive ────────────────────────────────────────────────────

    [Fact]
    public void IsExtractedArchive_ReturnsFalse_ForEmptySet()
    {
        var set = IngestArchiveContainerFilter.BuildExtractedSet([]);
        Assert.False(IngestArchiveContainerFilter.IsExtractedArchive(Abs("game.zip"), set));
    }

    [Fact]
    public void IsExtractedArchive_ReturnsTrue_ForMatchingPath()
    {
        var path = Abs("ps2/game.zip");
        var set  = IngestArchiveContainerFilter.BuildExtractedSet([Info(path)]);
        Assert.True(IngestArchiveContainerFilter.IsExtractedArchive(path, set));
    }

    [Fact]
    public void IsExtractedArchive_IsCaseInsensitive()
    {
        var lower = Abs("ps2/game.zip");
        var set   = IngestArchiveContainerFilter.BuildExtractedSet([Info(lower)]);
        // On Windows paths are case-insensitive; both normalise to the same full path.
        Assert.True(IngestArchiveContainerFilter.IsExtractedArchive(lower, set));
    }

    [Fact]
    public void IsExtractedArchive_ReturnsFalse_ForDifferentFile()
    {
        var zip1 = Abs("game1.zip");
        var zip2 = Abs("game2.zip");
        var set  = IngestArchiveContainerFilter.BuildExtractedSet([Info(zip1)]);
        Assert.False(IngestArchiveContainerFilter.IsExtractedArchive(zip2, set));
    }

    [Fact]
    public void IsExtractedArchive_ReturnsFalse_ForExtractedChild()
    {
        // The .zip is extracted; its child game.iso must NOT be filtered out.
        var archivePath = Abs("game.zip");
        var childPath   = Abs("game/game.iso");
        var set         = IngestArchiveContainerFilter.BuildExtractedSet([Info(archivePath)]);
        Assert.False(IngestArchiveContainerFilter.IsExtractedArchive(childPath, set));
    }

    [Fact]
    public void IsExtractedArchive_MultipleArchives_AllMatch()
    {
        var zip1 = Abs("disc1.zip");
        var zip2 = Abs("disc2.zip");
        var set  = IngestArchiveContainerFilter.BuildExtractedSet([Info(zip1), Info(zip2)]);
        Assert.True(IngestArchiveContainerFilter.IsExtractedArchive(zip1, set));
        Assert.True(IngestArchiveContainerFilter.IsExtractedArchive(zip2, set));
    }
}
