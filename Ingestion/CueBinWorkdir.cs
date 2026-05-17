using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>
/// Prepares a short-path temporary working directory for CUE/BIN → CHD transforms
/// so that chdman never sees long filenames in either the .cue path or the referenced
/// .bin paths.  The original source files are never modified.
/// </summary>
internal static class CueBinWorkdir
{
    // Matches a CUE FILE line: FILE "filename" TYPE
    // Group 1 = the filename inside the quotes.
    private static readonly Regex FileLineRegex =
        new(@"^\s*FILE\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Hardlink support ──────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    /// <summary>
    /// Creates an NTFS hardlink at <paramref name="linkPath"/> pointing to
    /// <paramref name="existingPath"/>.  Returns false (without throwing) on any failure
    /// so the caller can fall back to File.Copy.
    /// </summary>
    internal static bool TryHardLink(string linkPath, string existingPath)
    {
        try { return CreateHardLink(linkPath, existingPath, IntPtr.Zero); }
        catch { return false; }
    }

    // ── CUE rewrite (pure) ────────────────────────────────────────────────────

    /// <summary>
    /// Rewrites FILE lines in <paramref name="cueContent"/> to use short sequential track
    /// names (track01.bin, track02.bin, …), in the order they appear in the CUE.
    /// All other lines are returned verbatim, including line endings.
    /// </summary>
    /// <param name="cueContent">Full text of the source .cue file.</param>
    /// <param name="knownBinNames">
    /// Filenames of the known .bin dependencies (filename only, no directory).
    /// Matching against CUE FILE references is case-insensitive.
    /// </param>
    /// <param name="error">
    /// Set to a descriptive message when a FILE line references a filename not in
    /// <paramref name="knownBinNames"/>; null on success.
    /// </param>
    /// <returns>Rewritten CUE text, or null when <paramref name="error"/> is set.</returns>
    internal static string? RewriteCueContent(
        string                cueContent,
        IReadOnlyList<string> knownBinNames,
        out string?           error)
    {
        error = null;
        var sb           = new StringBuilder(cueContent.Length);
        int trackCounter = 0;
        var shortNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in SplitLines(cueContent))
        {
            var m = FileLineRegex.Match(line);
            if (!m.Success)
            {
                sb.Append(line);
                continue;
            }

            var originalName = m.Groups[1].Value;

            if (!shortNameMap.TryGetValue(originalName, out var shortName))
            {
                var known = knownBinNames.FirstOrDefault(b =>
                    string.Equals(b, originalName, StringComparison.OrdinalIgnoreCase));

                if (known is null)
                {
                    error = $"CUE FILE line references \"{originalName}\" which is not in the " +
                            $"known dependency list: " +
                            $"{string.Join(", ", knownBinNames.Select(b => $"\"{b}\""))}";
                    return null;
                }

                trackCounter++;
                shortName                = $"track{trackCounter:D2}.bin";
                shortNameMap[originalName] = shortName;
            }

            // Replace only the quoted filename token; preserve all other content
            // (leading whitespace, track type keyword, trailing whitespace, line endings).
            sb.Append(line.Replace($"\"{originalName}\"", $"\"{shortName}\""));
        }

        return sb.ToString();
    }

    // ── Workdir preparation ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a short-path working directory under
    /// <c>&lt;appRoot&gt;/transform-work/chd/&lt;jobId&gt;/</c>, copies each .bin into
    /// it with a short track name (track01.bin, …), and writes a rewritten
    /// <c>input.cue</c> that references those short names.
    /// The caller is responsible for deleting the workdir when finished.
    /// </summary>
    /// <returns>
    /// (true, workdirPath, null) on success; (false, workdirPath, errorMessage) on failure.
    /// The workdir path is always returned (even on failure) so callers can log or inspect it.
    /// </returns>
    /// <param name="hardlinkAttempt">
    /// Optional override for the hardlink operation — used in tests to inject a spy or
    /// force-failure.  When null the real <see cref="TryHardLink"/> is used.
    /// Signature: (linkPath, existingPath) → bool (true = hardlink created).
    /// </param>
    internal static (bool Success, string WorkdirPath, string? Error) PrepareWorkdir(
        string                       appRoot,
        string                       sourceDir,
        string                       cueName,
        IReadOnlyList<string>        binNames,
        Func<string, string, bool>?  hardlinkAttempt = null)
    {
        var jobId   = Guid.NewGuid().ToString("N")[..8];
        var workdir = Path.Combine(appRoot, "transform-work", "chd", jobId);

        try
        {
            Directory.CreateDirectory(workdir);

            // Read source CUE (never written back — original is untouched).
            var srcCuePath = Path.Combine(sourceDir, cueName);
            var cueContent = File.ReadAllText(srcCuePath);

            // Rewrite CUE to use short bin names.
            var rewritten = RewriteCueContent(cueContent, binNames, out var cueError);
            if (rewritten is null)
                return (false, workdir, $"CUE rewrite failed: {cueError}");

            // Build original→short mapping by scanning FILE lines in CUE order.
            // This is identical to the assignment done inside RewriteCueContent so
            // the copy and the rewrite always agree.
            int trackIdx = 0;
            var binMap   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in SplitLines(cueContent))
            {
                var m = FileLineRegex.Match(line);
                if (!m.Success) continue;
                var name = m.Groups[1].Value;
                if (!binMap.ContainsKey(name))
                {
                    trackIdx++;
                    binMap[name] = $"track{trackIdx:D2}.bin";
                }
            }

            // Link or copy each .bin to the workdir with its short name.
            // Prefer an NTFS hardlink (same inode, free on same volume) so chdman
            // can read the data without a byte-for-byte copy.  Falls back to
            // File.Copy when the hardlink cannot be created (cross-volume, FAT32, …).
            var doHardlink = hardlinkAttempt ?? TryHardLink;
            foreach (var (originalName, shortName) in binMap)
            {
                var srcBin  = Path.Combine(sourceDir, originalName);
                var destBin = Path.Combine(workdir,   shortName);
                if (!doHardlink(destBin, srcBin))
                    File.Copy(srcBin, destBin, overwrite: true);
            }

            // Write the rewritten CUE as input.cue (UTF-8, no BOM).
            File.WriteAllText(
                Path.Combine(workdir, "input.cue"),
                rewritten,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return (true, workdir, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, workdir, ex.Message);
        }
    }

    // ── Full pipeline ─────────────────────────────────────────────────────────

    /// <summary>
    /// Full CUE/BIN → CHD pipeline via a short-path workdir:
    /// <list type="number">
    ///   <item>Calls <see cref="PrepareWorkdir"/> to create the short workdir.</item>
    ///   <item>Runs the transform tool against <c>input.cue</c> → <c>output.chd</c>.</item>
    ///   <item>Moves <c>output.chd</c> to <paramref name="finalDestPath"/>.</item>
    ///   <item>Deletes the workdir on success (preserved on failure for debugging).</item>
    /// </list>
    /// </summary>
    /// <param name="workdirUsed">
    /// Path of the workdir created during the run.  Always set (even on failure) so
    /// the caller can include it in error messages.
    /// </param>
    internal static bool Run(
        string                appRoot,
        TransformRecord       xform,
        ToolRecord?           tool,
        string                sourceDir,
        string                cueName,
        IReadOnlyList<string> binNames,
        string                finalDestPath,
        out string            workdirUsed,
        out string            error)
    {
        var (prepOk, workdir, prepError) = PrepareWorkdir(appRoot, sourceDir, cueName, binNames);
        workdirUsed = workdir;

        if (!prepOk)
        {
            error = prepError ?? "Workdir preparation failed.";
            return false;
        }

        var workCuePath    = Path.Combine(workdir, "input.cue");
        var workOutputPath = Path.Combine(workdir, "output.chd");

        // Transform from short-path input.cue → short-path output.chd.
        // Workdir is preserved on failure so the caller can log its path.
        if (!TransformEngine.ExecuteTransform(xform, tool, appRoot, workCuePath, workOutputPath, out error))
            return false;

        // Move the produced CHD to its final archive location.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalDestPath)!);
            File.Move(workOutputPath, finalDestPath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"Failed to move output.chd to final path: {ex.Message}";
            return false;
        }

        // Clean up workdir only on full success.
        try { Directory.Delete(workdir, recursive: true); } catch { /* best-effort */ }
        error = "";
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits text into lines, preserving each line's original line ending
    /// (\n or \r\n) as part of the yielded string.
    /// The final fragment (if any) is yielded without a trailing newline.
    /// </summary>
    private static IEnumerable<string> SplitLines(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int j = text.IndexOf('\n', i);
            if (j < 0)
            {
                yield return text[i..];
                yield break;
            }
            yield return text[i..(j + 1)];
            i = j + 1;
        }
    }
}
