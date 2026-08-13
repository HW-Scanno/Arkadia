using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// M9B3 tests for the pure transform-dispatch decision extracted from the configured-leaf finalization
/// adapter. Proves the strategy → processor-path mapping is preserved and independent of any UI/selection
/// state (the resolver is a pure function of the persisted config), so a future Group run can finalize an
/// arbitrary leaf by supplying its own strategy/transform.
/// </summary>
public sealed class LeafTransformDispatchTests
{
    private static TransformRecord Xf(string processor, string outputKind, bool enabled = true)
        => new() { Id = "x", Name = "X", ProcessorType = processor, OutputKind = outputKind, IsEnabled = enabled };

    [Fact]  // (1) release_folder + folder-oriented file output → folder-file processor path
    public void ReleaseFolder_FileOutput_FolderFile()
        => Assert.Equal(LeafTransformDispatch.FolderFile,
            LeafTransformDispatchResolver.Decide("release_folder", Xf("folder_oriented", "file")));

    [Fact]  // (2) release_folder + folder-oriented folder output → folder-folder processor path
    public void ReleaseFolder_FolderOutput_FolderFolder()
        => Assert.Equal(LeafTransformDispatch.FolderFolder,
            LeafTransformDispatchResolver.Decide("release_folder", Xf("folder_oriented", "folder")));

    [Fact]  // (3) release_shape → release-shape processor
    public void ReleaseShape_Dispatch()
        => Assert.Equal(LeafTransformDispatch.ReleaseShape,
            LeafTransformDispatchResolver.Decide("release_shape", null));

    [Theory]  // (4) file-oriented fallback (none / file_extension)
    [InlineData("none")]
    [InlineData("file_extension")]
    public void FileOriented_Fallback(string strategy)
        => Assert.Equal(LeafTransformDispatch.FileOriented,
            LeafTransformDispatchResolver.Decide(strategy, null));

    [Fact]  // (5) release_folder with missing folder transform → config error
    public void ReleaseFolder_MissingTransform_ConfigError()
        => Assert.Equal(LeafTransformDispatch.FolderConfigError,
            LeafTransformDispatchResolver.Decide("release_folder", null));

    [Fact]  // (5) release_folder with a NON-folder-oriented transform → config error (not misrouted)
    public void ReleaseFolder_NonFolderTransform_ConfigError()
        => Assert.Equal(LeafTransformDispatch.FolderConfigError,
            LeafTransformDispatchResolver.Decide("release_folder", Xf("file_oriented", "file")));
}
