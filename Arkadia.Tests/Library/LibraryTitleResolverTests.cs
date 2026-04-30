using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Library;

public sealed class LibraryTitleResolverTests
{
    [Fact]
    public void DatMode_ReturnsRawName_WhenMetadataPresent()
    {
        var result = LibraryTitleResolver.Resolve("Super Mario Bros. (USA)", "dat", "Super Mario Bros.");
        Assert.Equal("Super Mario Bros. (USA)", result);
    }

    [Fact]
    public void CatalogMode_NoMetadata_FallsBackToRawName()
    {
        var result = LibraryTitleResolver.Resolve("Super Mario Bros. (USA)", "catalog", null);
        Assert.Equal("Super Mario Bros. (USA)", result);
    }

    [Fact]
    public void CatalogMode_EmptyMetadata_FallsBackToRawName()
    {
        var result = LibraryTitleResolver.Resolve("Super Mario Bros. (USA)", "catalog", "");
        Assert.Equal("Super Mario Bros. (USA)", result);
    }

    [Fact]
    public void CatalogMode_MetadataWithoutBracket_AppendsBracketFromRaw()
    {
        var result = LibraryTitleResolver.Resolve("Super Mario Bros. (USA)", "catalog", "Super Mario Bros.");
        Assert.Equal("Super Mario Bros. (USA)", result);
    }

    [Fact]
    public void CatalogMode_MetadataAlreadyHasBracket_NoDuplicateBracket()
    {
        var result = LibraryTitleResolver.Resolve("Super Mario Bros. (USA)", "catalog", "Super Mario Bros. (USA)");
        Assert.Equal("Super Mario Bros. (USA)", result);
    }

    [Fact]
    public void CatalogMode_RawNameNoBracket_ReturnsMetadataTitleOnly()
    {
        var result = LibraryTitleResolver.Resolve("super_mario_bros", "catalog", "Super Mario Bros.");
        Assert.Equal("Super Mario Bros.", result);
    }

    [Fact]
    public void CatalogMode_MultipleBracketsInRaw_AllAppended()
    {
        var result = LibraryTitleResolver.Resolve("Super Mario Bros. (USA) (Rev 1)", "catalog", "Super Mario Bros.");
        Assert.Equal("Super Mario Bros. (USA) (Rev 1)", result);
    }
}
