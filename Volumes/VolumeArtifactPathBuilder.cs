using System.IO;
using Arkadia.Data;

namespace Arkadia.Volumes;

/// <summary>
/// Central helper for constructing volume artifact file paths.
///
/// Final volume layout is always FLAT:
///   &lt;volume root&gt;\&lt;artifact filename&gt;
///
/// No release-name subfolder is ever created inside a volume root.
/// This class is the single authoritative source for volume destination paths;
/// use it in every write, read, verify, repair, and reabsorb code path.
/// </summary>
public static class VolumeArtifactPathBuilder
{
    /// <summary>
    /// Returns the absolute path of an artifact file within <paramref name="volumeRoot"/>.
    /// The file is always placed directly in the volume root — no subdirectory.
    /// </summary>
    public static string GetFlatFullPath(string volumeRoot, string artifactFileName)
        => Path.Combine(volumeRoot, artifactFileName);

    /// <summary>
    /// Returns the flat destination path for a Build Volume move.
    /// Build must place the artifact directly at the volume root — the release
    /// name is deliberately NOT used as a subfolder. This is the single
    /// production authority for Build's target path; call it from Build so the
    /// flat-layout rule is exercised by the same code the app runs.
    /// </summary>
    public static string GetBuildDestinationPath(string volumeRoot, ArtifactBuildInfo info)
        => GetFlatFullPath(volumeRoot, info.FileName);
}
