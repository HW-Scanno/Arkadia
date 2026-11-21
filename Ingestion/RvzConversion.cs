using System;
using System.IO;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>Logical identity the RVZ output must reconstruct: the physically hashed source ISO (size + digests).</summary>
public sealed record RvzExpectedLogical(long Size, string Sha1, string Md5, string Crc32);

/// <summary>Result of one verified ISO → RVZ conversion.</summary>
public sealed record RvzConversionResult(bool Success, string Error, string WorkdirUsed, bool InputHardlinked)
{
    public RvzLogicalHashes? Verified { get; init; }
}

/// <summary>
/// Verified ISO → RVZ transform (Phase A). Mirrors the <see cref="IsoChdWorkdir"/> safety pattern:
/// <list type="number">
///   <item>Private workdir <c>&lt;appRoot&gt;/transform-work/rvz/&lt;job&gt;/</c> — same filesystem as the archive
///         (both live under the app root), so promotion is a native rename.</item>
///   <item>The source ISO is never modified: it is exposed to the tool as an NTFS hardlink (zero-copy); when a
///         hardlink is impossible the tool reads the source path directly — never a byte copy.</item>
///   <item>DolphinTool writes <c>output.rvz</c> inside the workdir — never the final archive path.</item>
///   <item>Acceptance requires: a valid RVZ header whose logical size equals the source size and whose container
///         size equals the file length, AND <c>DolphinTool verify</c> digests (one streamed pass, no temp ISO)
///         equal to the source ISO's SHA1/MD5/CRC32. Anything else is a transform failure.</item>
///   <item>Only a verified output is promoted with <see cref="File.Move(string,string,bool)"/> (overwrite: false).</item>
///   <item>The workdir is always removed (its only contents are the hardlink name and our own output); the caller
///         keeps the source intact on any failure.</item>
/// </list>
/// </summary>
public static class RvzConversion
{
    public static RvzConversionResult Run(
        string                                         appRoot,
        TransformRecord                                xform,
        ToolRecord?                                    tool,
        string                                         sourceIsoPath,
        string                                         finalDestPath,
        RvzExpectedLogical                             expected,
        Func<string, string, bool>?                    hardlinkAttempt  = null,
        Func<string, string, (bool ok, string err)>?   convertOverride  = null,
        Func<string, DolphinVerifyResult>?             verifyOverride   = null)
    {
        var jobId   = Guid.NewGuid().ToString("N")[..8];
        var workdir = Path.Combine(appRoot, "transform-work", "rvz", jobId);
        bool hardlinked = false;

        RvzConversionResult Fail(string error)
        {
            TryDeleteWorkdir(workdir);
            return new RvzConversionResult(false, error, workdir, hardlinked);
        }

        try
        {
            if (File.Exists(finalDestPath) || Directory.Exists(finalDestPath))
                return new RvzConversionResult(false, $"archive target already exists: {finalDestPath}", workdir, false);

            // Resolve + portable-guard the real tool only when it is actually going to be invoked.
            string? exePath = null;
            if (convertOverride is null || verifyOverride is null)
            {
                if (tool is null) return Fail($"Transform '{xform.Name}' requires a tool but none is configured.");
                exePath = TransformEngine.ResolveToolExecutable(appRoot, tool);
                if (!DolphinToolInstallation.EnsurePortable(exePath, out var portableError)) return Fail(portableError);
            }

            Directory.CreateDirectory(workdir);

            var ext       = Path.GetExtension(sourceIsoPath);
            var linkInput = Path.Combine(workdir, "input" + (ext.Length > 0 ? ext : ".iso"));
            hardlinked = (hardlinkAttempt ?? CueBinWorkdir.TryHardLink)(linkInput, sourceIsoPath);
            var toolInput  = hardlinked ? linkInput : sourceIsoPath;   // never a byte copy
            var workOutput = Path.Combine(workdir, "output" + RvzIds.OutputExtension);

            // ── Convert (lossless; the template comes from the transform row) ──
            bool   ok;
            string convertError;
            if (convertOverride is not null)
                (ok, convertError) = convertOverride(toolInput, workOutput);
            else
                ok = TransformEngine.ExecuteTransform(xform, tool, appRoot, toolInput, workOutput, out convertError);
            if (!ok) return Fail($"[workdir {workdir}] conversion failed: {convertError}");

            if (!File.Exists(workOutput))                  return Fail($"[workdir {workdir}] conversion produced no output.rvz");
            var outLength = new FileInfo(workOutput).Length;
            if (outLength == 0)                            return Fail($"[workdir {workdir}] conversion produced a zero-byte output.rvz");

            // ── Structural check (72 bytes) ──
            if (!RvzHeader.TryRead(workOutput, out var header, out var headerError))
                return Fail($"[workdir {workdir}] output rejected: {headerError}");
            if (header.LogicalIsoSize != expected.Size)
                return Fail($"[workdir {workdir}] output rejected: logical size {header.LogicalIsoSize} ≠ source size {expected.Size}");

            // ── Logical verification (one streamed pass of the RVZ, no temp ISO) ──
            var verify = verifyOverride is not null ? verifyOverride(workOutput) : DolphinToolVerifier.Verify(exePath!, workOutput);
            if (!verify.Success || verify.Hashes is null)
                return Fail($"[workdir {workdir}] logical verification could not run: {verify.Error}");

            var mismatch = Compare(expected, verify.Hashes);
            if (mismatch is not null)
                return Fail($"[workdir {workdir}] output does not reconstruct the source ISO: {mismatch}");

            // ── Promote the verified output (native rename; never overwrite) ──
            Directory.CreateDirectory(Path.GetDirectoryName(finalDestPath)!);
            File.Move(workOutput, finalDestPath, overwrite: false);

            TryDeleteWorkdir(workdir);
            return new RvzConversionResult(true, "", workdir, hardlinked) { Verified = verify.Hashes };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail($"[workdir {workdir}] {ex.Message}");
        }
    }

    /// <summary>Null when every expected digest equals the logical digest; otherwise a description of the first mismatch.</summary>
    internal static string? Compare(RvzExpectedLogical expected, RvzLogicalHashes logical)
    {
        if (!Same(expected.Sha1, logical.Sha1))  return $"SHA1 expected {expected.Sha1} got {logical.Sha1}";
        if (expected.Md5.Length > 0   && logical.Md5.Length > 0 && !Same(expected.Md5, logical.Md5)) return $"MD5 expected {expected.Md5} got {logical.Md5}";
        if (expected.Crc32.Length > 0 && !Same(Crc32Hasher.Normalize(expected.Crc32), logical.Crc32))
            return $"CRC32 expected {expected.Crc32} got {logical.Crc32}";
        return null;
    }

    private static bool Same(string a, string b) =>
        a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes the private workdir. Safe: it only ever holds the input HARDLINK name (deleting a link name never
    /// deletes the source data) and our own output; never touches the source path itself.
    /// </summary>
    private static void TryDeleteWorkdir(string workdir)
    {
        try { if (Directory.Exists(workdir)) Directory.Delete(workdir, recursive: true); }
        catch { /* best-effort */ }
    }
}
