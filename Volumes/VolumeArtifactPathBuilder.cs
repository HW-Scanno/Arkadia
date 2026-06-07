using System.IO;

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
}
