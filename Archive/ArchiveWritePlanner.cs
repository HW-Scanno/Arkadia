using System.IO;

namespace Arkadia.Archive;

/// <summary>How an archive writer should resolve its target path.</summary>
public enum ArchiveWritePathAction
{
    /// <summary>No existing derived artifact for this content — write to the new policy path.</summary>
    WriteNew,
    /// <summary>A derived artifact already exists — keep its stored relative_path (idempotency; no orphan).</summary>
    UseExistingRelativePath,
}

/// <summary>Chosen archive write target for a release/artifact.</summary>
public sealed record ArchiveWritePlan(
    ArchiveWritePathAction Action,
    string RelativePath,
    string FullPath);

/// <summary>
/// Selects the archive write target, honoring the critical idempotency rule:
/// the new release-name-based naming policy applies only to NEW content. If a
/// derived artifact already exists for this content (an existing stored
/// <c>relative_path</c>), the writer keeps writing to that stored path — so
/// already-present artifacts are recognized and never orphaned or re-transformed
/// under a different filename, even when the new policy would name them differently.
///
/// Pure/DB-free: the caller supplies the existing stored relative_path (or null)
/// looked up from <c>derived_artifacts</c>, and the new policy path is built via
/// <see cref="ArchiveArtifactPathBuilder"/>.
/// </summary>
public static class ArchiveWritePlanner
{
    public static ArchiveWritePlan Plan(
        string appRoot,
        string platformId,
        string datLineId,
        ArchiveDatLineOutputForm form,
        string safeReleaseName,
        string newFileName,
        string? existingRelativePath)
    {
        if (!string.IsNullOrEmpty(existingRelativePath))
        {
            var full = Path.Combine(appRoot, existingRelativePath!.Replace('/', Path.DirectorySeparatorChar));
            return new ArchiveWritePlan(ArchiveWritePathAction.UseExistingRelativePath, existingRelativePath!, full);
        }

        var rel     = ArchiveArtifactPathBuilder.GetRelativePath(platformId, datLineId, form, safeReleaseName, newFileName);
        var newFull = ArchiveArtifactPathBuilder.GetFullPath(appRoot, platformId, datLineId, form, safeReleaseName, newFileName);
        return new ArchiveWritePlan(ArchiveWritePathAction.WriteNew, rel, newFull);
    }
}
