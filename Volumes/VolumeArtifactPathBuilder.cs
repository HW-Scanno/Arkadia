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

    // ── Group volume layout (additive; Single flat methods above are unchanged) ──
    // A Group volume nests artifacts one level under the leaf's physical segment:
    //   <volume root>\<leaf segment>\<artifact filename>
    // The same artifact filename may recur under different leaf segments (a distinct destination path). The
    // leaf segment is the authority of Arkadia.Volumes.GroupRelativeLeafPath — never the DatLineId verbatim.

    /// <summary>Group artifact path: <c>&lt;volumeRoot&gt;\&lt;leafSegment&gt;\&lt;artifactFileName&gt;</c>.</summary>
    public static string GetGroupFlatFullPath(string volumeRoot, string leafSegment, string artifactFileName)
        => Path.Combine(volumeRoot, leafSegment, artifactFileName);

    /// <summary>Group Build destination for one artifact (nested under its leaf segment).</summary>
    public static string GetGroupBuildDestinationPath(string volumeRoot, string leafSegment, ArtifactBuildInfo info)
        => GetGroupFlatFullPath(volumeRoot, leafSegment, info.FileName);
}
