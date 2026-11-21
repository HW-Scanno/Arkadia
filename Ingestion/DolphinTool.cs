using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Arkadia.Data;

namespace Arkadia.Ingestion;

/// <summary>Identifiers of the existing RVZ transform/tool rows (seeded by <c>CatalogService</c>; never renamed).</summary>
public static class RvzIds
{
    public const string TransformId     = "rvz_compression";
    public const string ToolId          = "dolphintool";
    public const string OutputExtension = ".rvz";

    public static bool IsRvzTransform(TransformRecord? t) => t is not null && t.Id == TransformId;

    /// <summary>
    /// Source-verified rule for the ONE case the generic "every declared DAT hash matches" rule cannot satisfy: the
    /// logical ISO source of an RVZ derived artifact whose MD5 DolphinTool legitimately did not compute
    /// (<c>MD5 not computed</c>, stored as NULL — never back-filled from the DAT). True only when the source is the
    /// one linked from that RVZ artifact, has no MD5, and its logical SHA1 equals the DAT SHA1, its logical size equals
    /// the DAT size, and its CRC32 equals the DAT CRC32 when declared. Any other case (computed MD5, other formats,
    /// missing SHA1/size) returns false so the caller's existing rule applies unchanged. Tier metadata is never used.
    /// </summary>
    public static bool IsVerifiedLogicalSourceWithoutMd5(
        ReleaseFileRecord f, SourceArtifactRecord? src, DerivedArtifactRecord? derived)
    {
        if (src is null || derived is null) return false;
        if (derived.StorageStrategyId != TransformId || derived.SourceArtifactId != src.Id) return false;
        if (src.HashedSourceMd5 is not null) return false;                         // MD5 computed → generic rule

        if (f.Sha1.Length == 0 ||
            !string.Equals(src.HashedSourceSha1, f.Sha1, StringComparison.OrdinalIgnoreCase)) return false;
        if (!long.TryParse(f.Size?.Trim(), System.Globalization.NumberStyles.None,
                           System.Globalization.CultureInfo.InvariantCulture, out var datSize) ||
            datSize != src.SourceSizeBytes) return false;
        var datCrc = Crc32Hasher.Normalize(f.Crc);
        if (datCrc.Length > 0 && datCrc != Crc32Hasher.Normalize(src.HashedSourceCrc32)) return false;
        return true;
    }
}

/// <summary>Captured outcome of one external-process run (stdout/stderr/exit code are all preserved).</summary>
public sealed record ToolProcessResult(int ExitCode, string StdOut, string StdErr, string? StartError)
{
    public bool Started => StartError is null;
}

/// <summary>
/// Runs DolphinTool as an isolated child process. Both streams are drained concurrently (no pipe-buffer deadlock),
/// arguments are passed via <see cref="ProcessStartInfo.ArgumentList"/> (no quoting/injection issues), and the
/// working directory is the tool folder so a <c>portable.txt</c> there is honoured.
/// </summary>
public static class DolphinToolProcess
{
    public static ToolProcessResult Run(string exePath, IReadOnlyList<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
                WorkingDirectory       = Path.GetDirectoryName(exePath) ?? "",
            };
            foreach (var a in arguments) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return new ToolProcessResult(-1, "", "", "Failed to start process.");

            Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderr = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            Task.WaitAll(stdout, stderr);
            return new ToolProcessResult(proc.ExitCode, stdout.Result, stderr.Result, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ToolProcessResult(-1, "", "", ex.Message);
        }
    }
}

/// <summary>
/// DolphinTool installation helpers. The executable lives at the registry-defined
/// <c>tools/&lt;folder_name&gt;/&lt;executable_name&gt;</c> (default <c>tools/dolphintool/DolphinTool.exe</c>).
/// Dolphin chooses its user folder via <c>UICommon::SetUserDirectory</c>: without <c>portable.txt</c> next to the
/// executable it may use <c>Documents\Dolphin Emulator</c> or <c>AppData\Roaming\Dolphin Emulator</c> and write the
/// <c>HKCU\Software\Dolphin Emulator\UserConfigPath</c> registry value. <see cref="EnsurePortable"/> guarantees the
/// tool runs in portable mode (user folder = <c>tools/dolphintool/User</c>) before any invocation.
/// </summary>
public static class DolphinToolInstallation
{
    public const string PortableMarker = "portable.txt";

    /// <summary>Absolute executable path when the registry row exists and the file is present; otherwise null.</summary>
    public static string? TryResolve(string appRoot, ToolRecord? tool)
    {
        if (tool is null || tool.FolderName.Length == 0 || tool.ExecutableName.Length == 0) return null;
        var path = Path.Combine(appRoot, "tools", tool.FolderName, tool.ExecutableName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Ensures <c>portable.txt</c> exists next to <paramref name="exePath"/> (creates an empty marker when absent;
    /// never modifies an existing one). Returns false with <paramref name="error"/> when it cannot be guaranteed, in
    /// which case the caller must not run the tool.
    /// </summary>
    public static bool EnsurePortable(string exePath, out string error)
    {
        error = "";
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) { error = "cannot resolve the DolphinTool folder"; return false; }
            var marker = Path.Combine(dir, PortableMarker);
            if (File.Exists(marker)) return true;
            using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            return File.Exists(marker);
        }
        catch (IOException) when (File.Exists(Path.Combine(Path.GetDirectoryName(exePath) ?? "", PortableMarker)))
        {
            return true;   // created concurrently
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"cannot create {PortableMarker} for DolphinTool portable mode: {ex.Message}";
            return false;
        }
    }
}

/// <summary>
/// Logical (reconstructed disc image) hashes reported by DolphinTool, lowercase hex. <see cref="Md5"/> is empty when
/// DolphinTool reported "MD5 not computed" (hardware-dependent default); SHA-1 and CRC32 are always present.
/// </summary>
public sealed record RvzLogicalHashes(string Sha1, string Md5, string Crc32);

/// <summary>Outcome of <c>DolphinTool verify</c>. <see cref="Success"/> false = TOOL failure, never a hash mismatch.</summary>
public sealed record DolphinVerifyResult(
    bool                  Success,
    RvzLogicalHashes?     Hashes,
    string                Error,
    bool                  ProblemsFound,
    IReadOnlyList<string> Problems)
{
    public static DolphinVerifyResult Fail(string error) => new(false, null, error, false, Array.Empty<string>());
}

/// <summary>
/// Parser for the full-report output of <c>DolphinTool verify -i FILE</c> (no <c>-a</c>), as printed by upstream
/// <c>VerifyCommand.cpp</c>: <c>CRC32: x</c> / <c>MD5: x</c> / <c>SHA1: x</c> (lowercase hex, or
/// "<c>&lt;ALG&gt; not computed</c>"), then <c>Problems Found: Yes|No</c>, then per-problem Severity/Summary lines.
/// SHA1, CRC32 and the "Problems Found" line are REQUIRED (an explicit "SHA1 not computed" is completed only by
/// <see cref="DolphinToolVerifier"/> via <c>-a sha1</c>); MD5 may be "not computed"; anything else (non-zero exit, missing/duplicated/
/// conflicting/invalid digest, truncated output) is reported as a tool failure. Reported problems are informational
/// only — acceptance is decided exclusively by digest equality against the DAT.
/// </summary>
public static class DolphinToolVerifyParser
{
    public static DolphinVerifyResult Parse(ToolProcessResult run)
    {
        var r = ParseReport(run, out var sha1NotComputed);
        return sha1NotComputed ? DolphinVerifyResult.Fail("DolphinTool did not compute a digest (SHA1 not computed)") : r;
    }

    /// <summary>
    /// Full-report parse that additionally tolerates an explicit <c>SHA1 not computed</c> line (CPU-dependent default
    /// in some DolphinTool builds). When <paramref name="sha1NotComputed"/> is true the result is otherwise fully
    /// validated but <c>Hashes.Sha1</c> is EMPTY: the caller MUST obtain SHA1 explicitly (<c>verify -a sha1</c>) or
    /// fail. Only <see cref="DolphinToolVerifier"/> uses this; <see cref="Parse"/> always fails in that case.
    /// </summary>
    internal static DolphinVerifyResult ParseReport(ToolProcessResult run, out bool sha1NotComputed)
    {
        sha1NotComputed = false;
        if (!run.Started) return DolphinVerifyResult.Fail($"DolphinTool could not be started: {run.StartError}");
        if (run.ExitCode != 0)
            return DolphinVerifyResult.Fail(
                $"DolphinTool verify exited with code {run.ExitCode}{FormatStderr(run.StdErr)}");

        string? crc = null, md5 = null, sha1 = null;
        bool md5Skipped = false, sha1Skipped = false;
        bool? problemsFound = null;
        var problems = new List<string>();

        foreach (var raw in run.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            // After "Problems Found:" only free-text problem lines follow — never accept digests from that section.
            if (problemsFound is not null)
            {
                if (TryValue(line, "Summary:", out var summary)) problems.Add(summary);
                continue;
            }

            // Observed with real DolphinTool 2609b: on CPUs with SHA-1 hardware acceleration the default verify report
            // skips MD5 ("MD5 not computed"). MD5 is optional (SHA-1 = identity key, CRC32 + size still checked);
            // a missing SHA-1 or CRC32 remains a tool failure.
            if (line == "MD5 not computed")
            {
                if (md5 is not null) return DolphinVerifyResult.Fail("malformed DolphinTool output (conflicting MD5 values)");
                md5Skipped = true;
                continue;
            }
            if (line == "SHA1 not computed")
            {
                if (sha1 is not null) return DolphinVerifyResult.Fail("malformed DolphinTool output (conflicting SHA1 values)");
                sha1Skipped = true;
                continue;
            }
            if (line.EndsWith(" not computed", StringComparison.Ordinal))
                return DolphinVerifyResult.Fail($"DolphinTool did not compute a digest ({line})");

            if (TryValue(line, "CRC32:", out var v))
            {
                if (!Accept(ref crc, v, 8, "CRC32", out var err)) return DolphinVerifyResult.Fail(err);
            }
            else if (TryValue(line, "MD5:", out v))
            {
                if (md5Skipped) return DolphinVerifyResult.Fail("malformed DolphinTool output (conflicting MD5 values)");
                if (!Accept(ref md5, v, 32, "MD5", out var err)) return DolphinVerifyResult.Fail(err);
            }
            else if (TryValue(line, "SHA1:", out v))
            {
                if (sha1Skipped) return DolphinVerifyResult.Fail("malformed DolphinTool output (conflicting SHA1 values)");
                if (!Accept(ref sha1, v, 40, "SHA1", out var err)) return DolphinVerifyResult.Fail(err);
            }
            else if (TryValue(line, "Problems Found:", out v))
            {
                if      (v == "Yes") problemsFound = true;
                else if (v == "No")  problemsFound = false;
                else return DolphinVerifyResult.Fail($"malformed DolphinTool output (Problems Found: '{v}')");
            }
        }

        if (crc is null || (md5 is null && !md5Skipped) || (sha1 is null && !sha1Skipped))
            return DolphinVerifyResult.Fail("malformed DolphinTool output (missing CRC32/MD5/SHA1)");
        if (problemsFound is null)
            return DolphinVerifyResult.Fail("malformed DolphinTool output (missing 'Problems Found' line — truncated?)");

        sha1NotComputed = sha1 is null;
        return new DolphinVerifyResult(true, new RvzLogicalHashes(sha1 ?? "", md5 ?? "", crc), "", problemsFound.Value, problems);
    }

    /// <summary>
    /// Parses the output of <c>DolphinTool verify -i FILE -a ALG</c>, which prints ONLY the bare lowercase digest.
    /// Accepts exactly one non-blank line of <paramref name="hexLength"/> hex characters; a multi-hash report
    /// ("SHA1: x" lines, "Problems Found", ...), extra lines, a non-zero exit or a start failure are rejected.
    /// </summary>
    internal static bool TryParseSingleDigest(ToolProcessResult run, int hexLength, out string digest, out string error)
    {
        digest = "";
        error  = "";
        if (!run.Started) { error = $"DolphinTool could not be started: {run.StartError}"; return false; }
        if (run.ExitCode != 0) { error = $"exited with code {run.ExitCode}{FormatStderr(run.StdErr)}"; return false; }
        var lines = new List<string>();
        foreach (var raw in run.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length > 0) lines.Add(line);
        }
        if (lines.Count != 1) { error = $"unexpected output ({lines.Count} non-blank lines, expected a single digest)"; return false; }
        var v = lines[0].ToLowerInvariant();
        if (v.Length != hexLength || !IsHex(v)) { error = $"unexpected output ('{lines[0]}' is not a digest)"; return false; }
        digest = v;
        return true;
    }

    private static bool TryValue(string line, string prefix, out string value)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = line[prefix.Length..].Trim();
            return true;
        }
        value = "";
        return false;
    }

    private static bool Accept(ref string? slot, string value, int hexLength, string name, out string error)
    {
        error = "";
        var v = value.ToLowerInvariant();
        if (v.Length != hexLength || !IsHex(v))
        {
            error = $"malformed DolphinTool output (invalid {name} '{value}')";
            return false;
        }
        if (slot is not null && slot != v)
        {
            error = $"malformed DolphinTool output (conflicting {name} values)";
            return false;
        }
        slot = v;
        return true;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    private static string FormatStderr(string stderr)
    {
        var t = stderr.Trim();
        return t.Length == 0 ? "" : $": {t}";
    }
}

/// <summary>
/// Runs <c>DolphinTool verify -i "&lt;file&gt;"</c> once (all default digests in a single streamed pass, no temp file).
/// Only if that report explicitly says <c>SHA1 not computed</c> is a second <c>verify -a sha1</c> pass run.
/// </summary>
public static class DolphinToolVerifier
{
    public static DolphinVerifyResult Verify(
        string                                                  exePath,
        string                                                  inputPath,
        Func<string, IReadOnlyList<string>, ToolProcessResult>? runner = null)
    {
        if (!DolphinToolInstallation.EnsurePortable(exePath, out var portableError))
            return DolphinVerifyResult.Fail(portableError);
        var exec   = runner ?? DolphinToolProcess.Run;
        var report = DolphinToolVerifyParser.ParseReport(exec(exePath, new[] { "verify", "-i", inputPath }),
                                                         out var sha1NotComputed);
        if (!sha1NotComputed) return report;                       // normal path: exactly one verify pass

        // Compatibility fallback, ONLY when the default report explicitly said "SHA1 not computed": request SHA1
        // explicitly (one more streamed read of the RVZ; no temp ISO, the RVZ is never written). CRC32/MD5/problems
        // from the first report are reused.
        var fallback = exec(exePath, new[] { "verify", "-i", inputPath, "-a", "sha1" });
        if (!DolphinToolVerifyParser.TryParseSingleDigest(fallback, 40, out var sha1, out var error))
            return DolphinVerifyResult.Fail(
                $"DolphinTool verify did not compute SHA1 and the explicit 'verify -a sha1' fallback failed: {error}");
        return report with { Hashes = report.Hashes! with { Sha1 = sha1 } };
    }
}

/// <summary>Header facts of an RVZ container (read from the first 0x48 bytes only).</summary>
public readonly record struct RvzHeaderInfo(long LogicalIsoSize, long ContainerFileSize);

/// <summary>
/// Minimal RVZ header reader (upstream <c>DiscIO/WIABlob.h</c> <c>WIAHeader1</c>, packed, 0x48 bytes, big-endian):
/// magic "RVZ\x01" @0x00, iso_file_size u64 @0x24, wia_file_size u64 @0x2C. Reads 72 bytes — never a full-file pass.
/// Rejects non-RVZ (including WIA) magic, a non-positive logical size, and a declared container size that differs
/// from the physical file length (truncated/corrupt container; Dolphin itself refuses such files).
/// </summary>
public static class RvzHeader
{
    public const int HeaderSize = 0x48;
    private static readonly byte[] Magic = { (byte)'R', (byte)'V', (byte)'Z', 0x01 };

    public static bool TryRead(string path, out RvzHeaderInfo info, out string error)
    {
        info  = default;
        error = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None);
            var physicalLength = fs.Length;
            var buf = new byte[HeaderSize];
            int read = 0;
            while (read < HeaderSize)
            {
                int n = fs.Read(buf, read, HeaderSize - read);
                if (n == 0) break;
                read += n;
            }
            if (read < HeaderSize) { error = "file too small for an RVZ header"; return false; }
            for (int i = 0; i < Magic.Length; i++)
                if (buf[i] != Magic[i]) { error = "not an RVZ container (magic mismatch)"; return false; }

            long iso = ReadBe64(buf, 0x24);
            long wia = ReadBe64(buf, 0x2C);
            if (iso <= 0) { error = "RVZ header declares no logical disc size"; return false; }
            if (wia != physicalLength)
            {
                error = $"RVZ header declares {wia} bytes but the file is {physicalLength} bytes (truncated or corrupt)";
                return false;
            }
            info = new RvzHeaderInfo(iso, wia);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"cannot read RVZ header: {ex.Message}";
            return false;
        }
    }

    private static long ReadBe64(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
        return v > long.MaxValue ? -1 : (long)v;
    }

    /// <summary>Builds a header-only RVZ prefix (tests/fixtures): magic + logical size + container size.</summary>
    internal static byte[] BuildForTests(long logicalIsoSize, long containerFileSize)
    {
        var b = new byte[HeaderSize];
        Magic.CopyTo(b, 0);
        WriteBe64(b, 0x24, (ulong)logicalIsoSize);
        WriteBe64(b, 0x2C, (ulong)containerFileSize);
        return b;
    }

    private static void WriteBe64(byte[] b, int o, ulong v)
    {
        for (int i = 7; i >= 0; i--) { b[o + i] = (byte)(v & 0xFF); v >>= 8; }
    }
}
