using System;
using System.IO;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>
/// Prepares a short-path temporary working directory for SingleIso → CHD transforms,
/// mirroring the safety guarantees of <see cref="CueBinWorkdir"/>:
/// <list type="bullet">
///   <item>chdman writes to an isolated workdir, never directly to the final archive path.</item>
///   <item>The source ISO is never modified — it is materialized as a hardlink or read-only copy.</item>
///   <item>The final .chd appears in archive only after a verified, successful output.chd is moved.</item>
///   <item>The workdir is preserved on failure so callers can log its path for debugging.</item>
/// </list>
/// </summary>
internal static class IsoChdWorkdir
{
    /// <summary>
    /// Full SingleIso → CHD pipeline via a short-path workdir:
    /// <list type="number">
    ///   <item>Creates a fresh workdir at <c>&lt;appRoot&gt;/transform-work/chd/&lt;jobId&gt;/</c>.</item>
    ///   <item>Materializes the source ISO as <c>input.iso</c> — hardlink if possible, copy otherwise.</item>
    ///   <item>Runs the transform tool: <c>input.iso</c> → <c>output.chd</c> (inside workdir).</item>
    ///   <item>Verifies <c>output.chd</c> exists and is non-empty.</item>
    ///   <item>Moves <c>output.chd</c> to <paramref name="finalDestPath"/> atomically (overwrite:true).</item>
    ///   <item>Deletes the workdir on success; preserves it on failure for debugging.</item>
    /// </list>
    /// The caller is responsible for <paramref name="finalDestPath"/>'s parent directory.
    /// </summary>
    /// <param name="workdirUsed">
    /// Workdir path.  Always set — even on failure — so the caller can include it in error messages.
    /// </param>
    /// <param name="inputHardlinked">
    /// True when the ISO was hardlinked into the workdir (cost-free on same NTFS volume);
    /// false when a full byte copy was made.  Returned for logging.
    /// </param>
    /// <param name="error">Descriptive error on failure; empty string on success.</param>
    /// <param name="hardlinkAttempt">
    /// Injectable hardlink function for tests.  Null uses <see cref="CueBinWorkdir.TryHardLink"/>.
    /// Signature: (linkPath, existingPath) → bool (true = hardlink created).
    /// </param>
    /// <param name="executeTransformOverride">
    /// Injectable transform executor for tests.  Null uses <see cref="TransformEngine.ExecuteTransform"/>.
    /// Signature: (inputPath, outputPath) → (bool success, string errorMessage).
    /// </param>
    internal static bool Run(
        string                                        appRoot,
        TransformRecord                               xform,
        ToolRecord?                                   tool,
        string                                        sourceIsoPath,
        string                                        finalDestPath,
        out string                                    workdirUsed,
        out bool                                      inputHardlinked,
        out string                                    error,
        Func<string, string, bool>?                   hardlinkAttempt           = null,
        Func<string, string, (bool ok, string err)>?  executeTransformOverride  = null)
    {
        var jobId   = Guid.NewGuid().ToString("N")[..8];
        var workdir = Path.Combine(appRoot, "transform-work", "chd", jobId);
        workdirUsed     = workdir;
        inputHardlinked = false;

        try
        {
            Directory.CreateDirectory(workdir);

            var workInputPath  = Path.Combine(workdir, "input.iso");
            var workOutputPath = Path.Combine(workdir, "output.chd");

            // Materialize source ISO in workdir.
            // Prefer an NTFS hardlink (same-volume, near-zero cost) so chdman gets a short
            // path without a byte-for-byte copy of a potentially multi-GB file.
            var doHardlink  = hardlinkAttempt ?? CueBinWorkdir.TryHardLink;
            inputHardlinked = doHardlink(workInputPath, sourceIsoPath);
            if (!inputHardlinked)
                File.Copy(sourceIsoPath, workInputPath, overwrite: true);

            // Transform input.iso → output.chd.
            // Archive is untouched until we call File.Move after verified success.
            bool   ok;
            string transformError;
            if (executeTransformOverride is not null)
            {
                (ok, transformError) = executeTransformOverride(workInputPath, workOutputPath);
            }
            else
            {
                ok = TransformEngine.ExecuteTransform(
                    xform, tool, appRoot, workInputPath, workOutputPath, out transformError);
            }

            if (!ok)
            {
                // Preserve workdir for debugging; report full context in error.
                error = $"[workdir {workdir}; input {workInputPath}; output {workOutputPath}]: {transformError}";
                return false;
            }

            if (!File.Exists(workOutputPath))
            {
                error = $"Transform exited cleanly but output.chd not found in workdir {workdir}";
                return false;
            }

            if (new FileInfo(workOutputPath).Length == 0)
            {
                error = $"Transform produced a zero-byte output.chd in workdir {workdir}";
                return false;
            }

            // Commit: atomic move to final archive path.
            // overwrite:true means any stale partial .chd from a previous crashed run is
            // replaced only after the new output is fully written and verified.
            Directory.CreateDirectory(Path.GetDirectoryName(finalDestPath)!);
            File.Move(workOutputPath, finalDestPath, overwrite: true);

            // Clean up workdir only after successful commit.
            try { Directory.Delete(workdir, recursive: true); } catch { /* best-effort */ }

            error = "";
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"[workdir {workdir}] {ex.Message}";
            return false;
        }
    }
}
