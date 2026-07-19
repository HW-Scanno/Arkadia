using System.IO;

namespace Arkadia.Archive;

/// <summary>
/// Single production authority for archive artifact write paths. Mirrors
/// <see cref="Volumes.VolumeArtifactPathBuilder"/> for the archive domain.
///
/// SingleFileFlat:          archive/&lt;platform&gt;/&lt;datLine&gt;/&lt;fileName&gt;
/// MultiFileReleaseFolder:  archive/&lt;platform&gt;/&lt;datLine&gt;/&lt;safeReleaseName&gt;/&lt;fileName&gt;
///
/// For SingleFileFlat, callers pass a release-name-based fileName. For
/// MultiFileReleaseFolder, callers pass the original inner file name.
///
/// The relative path uses forward slashes to match how ingestion persists
/// <c>derived_artifacts.relative_path</c>; the full path uses OS separators.
/// </summary>
public static class ArchiveArtifactPathBuilder
{
    public static string GetRelativePath(
        string platformId,
        string datLineId,
        ArchiveDatLineOutputForm form,
        string safeReleaseName,
        string fileName)
    {
        return form == ArchiveDatLineOutputForm.MultiFileReleaseFolder
            ? $"archive/{platformId}/{datLineId}/{safeReleaseName}/{fileName}"
            : $"archive/{platformId}/{datLineId}/{fileName}";
    }

    public static string GetFullPath(
        string appRoot,
        string platformId,
        string datLineId,
        ArchiveDatLineOutputForm form,
        string safeReleaseName,
        string fileName)
    {
        var rel = GetRelativePath(platformId, datLineId, form, safeReleaseName, fileName);
        return Path.Combine(appRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Relative path of the release-folder ROOT for a MultiFileReleaseFolder DAT line —
    /// <c>archive/&lt;platform&gt;/&lt;datLine&gt;/&lt;safeReleaseName&gt;</c>. The derived
    /// artifact IS this folder; its inner files keep their original names underneath it.
    /// Forward slashes, matching how <c>derived_artifacts.relative_path</c> is persisted.
    /// </summary>
    public static string GetReleaseFolderRoot(
        string platformId,
        string datLineId,
        string safeReleaseName)
        => $"archive/{platformId}/{datLineId}/{safeReleaseName}";

    /// <summary>OS-separated full path of the release-folder root. See <see cref="GetReleaseFolderRoot"/>.</summary>
    public static string GetReleaseFolderRootFullPath(
        string appRoot,
        string platformId,
        string datLineId,
        string safeReleaseName)
    {
        var rel = GetReleaseFolderRoot(platformId, datLineId, safeReleaseName);
        return Path.Combine(appRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    }
}
