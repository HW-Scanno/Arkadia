using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// The single shared archive-container authority (consolidated from the Group planner + Single RunPreIngest).
/// Exactly <c>.zip / .7z / .rar</c>, case-insensitive — no more, no fewer.
/// </summary>
public sealed class ArchiveContainerExtensionsTests
{
    [Theory]
    [InlineData("a.zip")]
    [InlineData("a.7z")]
    [InlineData("a.rar")]
    [InlineData("A.ZIP")]
    [InlineData("A.7Z")]
    [InlineData("dir/sub/thing.RaR")]
    public void IsContainer_True_ForSupportedExtensions(string path)
        => Assert.True(ArchiveContainerExtensions.IsContainer(path));

    [Theory]
    [InlineData("a.d64")]
    [InlineData("a.arc")]
    [InlineData("a.ark")]
    [InlineData("a.tar")]
    [InlineData("a.gz")]
    [InlineData("a")]
    [InlineData("a.zipx")]
    public void IsContainer_False_ForEverythingElse(string path)
        => Assert.False(ArchiveContainerExtensions.IsContainer(path));

    [Fact]
    public void All_ContainsExactlyTheThreeSupportedExtensions()
    {
        Assert.Equal(3, ArchiveContainerExtensions.All.Count);
        Assert.Contains(".zip", ArchiveContainerExtensions.All);
        Assert.Contains(".7z",  ArchiveContainerExtensions.All);
        Assert.Contains(".rar", ArchiveContainerExtensions.All);
    }

    [Fact]
    public void All_IsCaseInsensitive()
    {
        Assert.Contains(".ZIP", ArchiveContainerExtensions.All);
        Assert.Contains(".Rar", ArchiveContainerExtensions.All);
    }
}
