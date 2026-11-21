using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// DolphinTool integration primitives (RVZ-SUPPORT-01): verify-output parser, RVZ header reader, portable-mode
/// guard, tool resolution and the verifier wrapper. No real DolphinTool binary is required or executed.
/// </summary>
public sealed class DolphinToolTests : IDisposable
{
    private readonly string _dir;

    public DolphinToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkDolphin_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";
    private const string Md5  = "0123456789abcdef0123456789abcdef";
    private const string Crc  = "89abcdef";

    // Exact upstream VerifyCommand.cpp full-report shape.
    private static string Report(string problems = "Problems Found: No\n") =>
        $"CRC32: {Crc}\nMD5: {Md5}\nSHA1: {Sha1}\n{problems}";

    private static ToolProcessResult Ok(string stdout) => new(0, stdout, "", null);

    // ── Parser ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_FullReport_ExtractsAllThreeDigests()
    {
        var r = DolphinToolVerifyParser.Parse(Ok(Report()));
        Assert.True(r.Success);
        Assert.Equal(new RvzLogicalHashes(Sha1, Md5, Crc), r.Hashes);
        Assert.False(r.ProblemsFound);
    }

    [Fact]
    public void Parse_CrLfAndUppercase_AreNormalized()
    {
        var stdout = $"CRC32: {Crc.ToUpperInvariant()}\r\nMD5: {Md5.ToUpperInvariant()}\r\nSHA1: {Sha1.ToUpperInvariant()}\r\nProblems Found: No\r\n";
        var r = DolphinToolVerifyParser.Parse(Ok(stdout));
        Assert.True(r.Success, r.Error);
        Assert.Equal(Sha1, r.Hashes!.Sha1);
    }

    [Fact]
    public void Parse_ProblemsAreInformationalOnly()
    {
        var r = DolphinToolVerifyParser.Parse(Ok(Report(
            "Problems Found: Yes\n\nSeverity: Low\nSummary: The disc image is scrubbed.\n\n")));
        Assert.True(r.Success);
        Assert.True(r.ProblemsFound);
        Assert.Equal(new[] { "The disc image is scrubbed." }, r.Problems);
    }

    [Fact]
    public void Parse_DigestLikeLineInsideProblemSection_IsNeverAccepted()
    {
        var r = DolphinToolVerifyParser.Parse(Ok(Report(
            "Problems Found: Yes\n\nSeverity: Low\nSummary: x\nSHA1: ffffffffffffffffffffffffffffffffffffffff\n")));
        Assert.True(r.Success);
        Assert.Equal(Sha1, r.Hashes!.Sha1);
    }

    /// <summary>
    /// Verbatim stdout of real DolphinTool 2609b (x64, Windows, SHA-1 hardware-accelerated CPU) for
    /// <c>verify -i smoke.rvz</c> on a synthetic 8 MiB GameCube image: CRLF line endings, MD5 skipped by default.
    /// </summary>
    private const string Real2609bVerifyStdout =
        "CRC32: 0cb5d407\r\nMD5 not computed\r\nSHA1: 1dd6b5f346ccdd096751a7a822e9ed2ea4650e0d\r\nProblems Found: Yes\r\n\r\n" +
        "Severity: Low\r\nSummary: This disc image has an unusual size. This will likely make the emulated loading times " +
        "longer. You will likely be unable to share input recordings and use NetPlay with anyone who is using a good dump.\r\n\r\n";

    [Fact]
    public void Parse_RealDolphinTool2609bOutput_Md5NotComputed_IsAccepted()
    {
        var r = DolphinToolVerifyParser.Parse(Ok(Real2609bVerifyStdout));
        Assert.True(r.Success, r.Error);
        Assert.Equal("1dd6b5f346ccdd096751a7a822e9ed2ea4650e0d", r.Hashes!.Sha1);
        Assert.Equal("0cb5d407", r.Hashes.Crc32);
        Assert.Equal("", r.Hashes.Md5);                                   // not computed — never fabricated
        Assert.True(r.ProblemsFound);
        Assert.Single(r.Problems);
    }

    [Theory]
    [InlineData("CRC32: 89abcdef\nMD5: 0123456789abcdef0123456789abcdef\nSHA1 not computed\nProblems Found: No\n")]  // SHA1 is mandatory
    [InlineData("CRC32: 89abcdef\nMD5 not computed\nMD5: 0123456789abcdef0123456789abcdef\nSHA1: 0123456789abcdef0123456789abcdef01234567\nProblems Found: No\n")]
    [InlineData("CRC32: 89abcdef\nMD5: 0123456789abcdef0123456789abcdef\nMD5 not computed\nSHA1: 0123456789abcdef0123456789abcdef01234567\nProblems Found: No\n")]
    [InlineData("CRC32 not computed\nMD5: 0123456789abcdef0123456789abcdef\nSHA1: 0123456789abcdef0123456789abcdef01234567\nProblems Found: No\n")]
    [InlineData("CRC32: 89abcdef\nMD5: 0123456789abcdef0123456789abcdef\nProblems Found: No\n")]                      // SHA1 missing
    [InlineData("CRC32: 89abcdef\nMD5: 0123456789abcdef0123456789abcdef\nSHA1: 0123\nProblems Found: No\n")]           // short SHA1
    [InlineData("CRC32: 89abcdef\nMD5: 0123456789abcdef0123456789abcdef\nSHA1: zz23456789abcdef0123456789abcdef01234567\nProblems Found: No\n")]
    [InlineData("CRC32: 89abcdef\nMD5: 0123456789abcdef0123456789abcdef\nSHA1: 0123456789abcdef0123456789abcdef01234567\n")]  // truncated
    [InlineData("CRC32: 89abcdef\nCRC32: 00000000\nMD5: 0123456789abcdef0123456789abcdef\nSHA1: 0123456789abcdef0123456789abcdef01234567\nProblems Found: No\n")]
    [InlineData("CRC32: 89abcdef\nMD5: 0123456789abcdef0123456789abcdef\nSHA1: 0123456789abcdef0123456789abcdef01234567\nProblems Found: Maybe\n")]
    [InlineData("")]
    [InlineData("garbage output")]
    public void Parse_MalformedOutput_IsToolFailure(string stdout)
    {
        var r = DolphinToolVerifyParser.Parse(Ok(stdout));
        Assert.False(r.Success);
        Assert.Null(r.Hashes);
        Assert.NotEmpty(r.Error);
    }

    [Fact]
    public void Parse_NonZeroExit_IsToolFailure_WithStderr()
    {
        var r = DolphinToolVerifyParser.Parse(new ToolProcessResult(1, Report(), "Error: Unable to open input file\n", null));
        Assert.False(r.Success);
        Assert.Contains("code 1", r.Error);
        Assert.Contains("Unable to open input file", r.Error);
    }

    [Fact]
    public void Parse_ProcessNotStarted_IsToolFailure()
    {
        var r = DolphinToolVerifyParser.Parse(new ToolProcessResult(-1, "", "", "file not found"));
        Assert.False(r.Success);
        Assert.Contains("could not be started", r.Error);
    }

    [Fact]
    public void Process_MissingExecutable_ReportsStartError_NoThrow()
    {
        var run = DolphinToolProcess.Run(Path.Combine(_dir, "nope", "DolphinTool.exe"), new[] { "verify" });
        Assert.False(run.Started);
    }

    // ── Verifier wrapper ──────────────────────────────────────────────────────

    [Fact]
    public void Verifier_RunsVerifyOnce_WithExactArguments_AndEnsuresPortableMode()
    {
        var exe = Path.Combine(_dir, "tools", "dolphintool", "DolphinTool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "stub");

        var calls = new List<(string Exe, IReadOnlyList<string> Args)>();
        var r = DolphinToolVerifier.Verify(exe, @"C:\in put\Game (USA).rvz", (e, a) => { calls.Add((e, a)); return Ok(Report()); });

        Assert.True(r.Success);
        var call = Assert.Single(calls);
        Assert.Equal(exe, call.Exe);
        Assert.Equal(new[] { "verify", "-i", @"C:\in put\Game (USA).rvz" }, call.Args);   // no -a: all digests in one pass
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "portable.txt")));
    }

    // ── SHA-1 compatibility fallback (simulated CPU-dependent DolphinTool defaults) ────────────────────────

    private const string NoSha1Report = $"CRC32: {Crc}\nMD5 not computed\nSHA1 not computed\nProblems Found: Yes\n\nSeverity: Low\nSummary: The disc image is scrubbed.\n\n";
    private const string FallbackSha1 = "fedcba9876543210fedcba9876543210fedcba98";

    private string StubExe()
    {
        var exe = Path.Combine(_dir, "tools", "dolphintool", "DolphinTool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "stub");
        return exe;
    }

    private static bool IsSha1Fallback(IReadOnlyList<string> a) => a.Count == 5 && a[3] == "-a" && a[4] == "sha1";

    [Fact]
    public void Verifier_Sha1NotComputed_FallsBackToExplicitSha1_ReusingOtherMetadata()
    {
        var exe   = StubExe();
        var calls = new List<IReadOnlyList<string>>();
        var r = DolphinToolVerifier.Verify(exe, @"C:\in\Game.rvz", (_, a) =>
        {
            calls.Add(a);
            // `-a sha1` prints only the bare digest (format observed with real DolphinTool 2609b: digest + CRLF CRLF).
            return Ok(IsSha1Fallback(a) ? FallbackSha1 + "\r\n\r\n" : NoSha1Report);
        });

        Assert.True(r.Success, r.Error);
        Assert.Equal(FallbackSha1, r.Hashes!.Sha1);
        Assert.Equal(Crc, r.Hashes.Crc32);                        // reused from the first report
        Assert.Equal("", r.Hashes.Md5);                           // still not computed — never fabricated
        Assert.True(r.ProblemsFound);
        Assert.Equal(new[] { "The disc image is scrubbed." }, r.Problems);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "verify", "-i", @"C:\in\Game.rvz" }, calls[0]);
        Assert.Equal(new[] { "verify", "-i", @"C:\in\Game.rvz", "-a", "sha1" }, calls[1]);
    }

    [Fact]
    public void Verifier_Sha1AlreadyComputed_NeverRunsFallback()
    {
        var exe = StubExe();
        foreach (var stdout in new[] { Report(), Real2609bVerifyStdout })
        {
            var calls = new List<IReadOnlyList<string>>();
            var r = DolphinToolVerifier.Verify(exe, "x.rvz", (_, a) => { calls.Add(a); return Ok(stdout); });
            Assert.True(r.Success, r.Error);
            Assert.Single(calls);
            Assert.DoesNotContain(calls, IsSha1Fallback);
        }
    }

    [Theory]
    [InlineData(1, "", "Error: Unable to open input file\n")]                                         // tool error
    [InlineData(0, "", "")]                                                                          // empty
    [InlineData(0, "SHA1: fedcba9876543210fedcba9876543210fedcba98\nProblems Found: No\n", "")]       // multi-hash shape
    [InlineData(0, "fedcba9876543210fedcba9876543210fedcba98\nfedcba9876543210fedcba9876543210fedcba98\n", "")]
    [InlineData(0, "fedcba98\n", "")]                                                                // not a SHA1
    [InlineData(0, "SHA1 not computed\n", "")]
    public void Verifier_Sha1FallbackFailure_IsExplicitToolFailure(int exit, string fallbackStdout, string fallbackStderr)
    {
        var exe = StubExe();
        var r = DolphinToolVerifier.Verify(exe, "x.rvz", (_, a) =>
            IsSha1Fallback(a) ? new ToolProcessResult(exit, fallbackStdout, fallbackStderr, null) : Ok(NoSha1Report));

        Assert.False(r.Success);
        Assert.Null(r.Hashes);
        Assert.Contains("verify -a sha1", r.Error);
    }

    [Fact]
    public void Verifier_Sha1NotComputed_ButReportOtherwiseInvalid_FailsWithoutFallback()
    {
        var exe = StubExe();
        var calls = new List<IReadOnlyList<string>>();
        var r = DolphinToolVerifier.Verify(exe, "x.rvz", (_, a) =>
        {
            calls.Add(a);
            return Ok($"CRC32: {Crc}\nMD5: {Md5}\nSHA1 not computed\n");     // truncated: no "Problems Found"
        });
        Assert.False(r.Success);
        Assert.Single(calls);
    }

    [Theory]
    [InlineData("CRC32: 89abcdef\nMD5: 0123456789abcdef0123456789abcdef\nSHA1 not computed\nSHA1: 0123456789abcdef0123456789abcdef01234567\nProblems Found: No\n")]
    [InlineData("CRC32: 89abcdef\nMD5: 0123456789abcdef0123456789abcdef\nSHA1: 0123456789abcdef0123456789abcdef01234567\nSHA1 not computed\nProblems Found: No\n")]
    public void Verifier_ConflictingSha1Lines_FailWithoutFallback(string stdout)
    {
        var exe = StubExe();
        var calls = 0;
        var r = DolphinToolVerifier.Verify(exe, "x.rvz", (_, _) => { calls++; return Ok(stdout); });
        Assert.False(r.Success);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Parse_PublicFullReportParser_StillRejectsSha1NotComputed()
    {
        var r = DolphinToolVerifyParser.Parse(Ok(NoSha1Report));
        Assert.False(r.Success);
        Assert.Contains("SHA1 not computed", r.Error);
    }

    // ── Installation ──────────────────────────────────────────────────────────

    private static ToolRecord SeededTool() =>
        new() { Id = "dolphintool", FolderName = "dolphintool", ExecutableName = "DolphinTool.exe", IsBundled = true };

    [Fact]
    public void TryResolve_MissingExecutable_ReturnsNull()
    {
        Assert.Null(DolphinToolInstallation.TryResolve(_dir, SeededTool()));
        Assert.Null(DolphinToolInstallation.TryResolve(_dir, null));
    }

    [Fact]
    public void TryResolve_UsesRegistryConvention_ToolsFolderExecutable()
    {
        var exe = Path.Combine(_dir, "tools", "dolphintool", "DolphinTool.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "stub");
        Assert.Equal(exe, DolphinToolInstallation.TryResolve(_dir, SeededTool()));
    }

    [Fact]
    public void EnsurePortable_CreatesMarkerOnce_NeverRewritesExisting()
    {
        var exe = Path.Combine(_dir, "DolphinTool.exe");
        File.WriteAllText(exe, "stub");
        var marker = Path.Combine(_dir, "portable.txt");

        Assert.True(DolphinToolInstallation.EnsurePortable(exe, out _));
        Assert.True(File.Exists(marker));
        Assert.Equal(0, new FileInfo(marker).Length);

        File.WriteAllText(marker, "user content");
        Assert.True(DolphinToolInstallation.EnsurePortable(exe, out _));
        Assert.Equal("user content", File.ReadAllText(marker));
    }

    // ── RVZ header ────────────────────────────────────────────────────────────

    private string WriteRvz(string name, long logicalSize, int payload = 100, long? declaredContainer = null)
    {
        var path  = Path.Combine(_dir, name);
        var total = RvzHeader.HeaderSize + payload;
        var bytes = new byte[total];
        RvzHeader.BuildForTests(logicalSize, declaredContainer ?? total).CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Header_ValidRvz_ReadsLogicalSize_BigEndian()
    {
        var p = WriteRvz("a.rvz", 4_699_979_776L);
        Assert.True(RvzHeader.TryRead(p, out var h, out var err), err);
        Assert.Equal(4_699_979_776L, h.LogicalIsoSize);
        Assert.Equal(new FileInfo(p).Length, h.ContainerFileSize);
    }

    [Fact]
    public void Header_WiaMagic_IsRejected()
    {
        var p = WriteRvz("w.rvz", 1000);
        var b = File.ReadAllBytes(p);
        b[0] = (byte)'W'; b[1] = (byte)'I'; b[2] = (byte)'A';
        File.WriteAllBytes(p, b);
        Assert.False(RvzHeader.TryRead(p, out _, out var err));
        Assert.Contains("magic", err);
    }

    [Fact]
    public void Header_TruncatedContainer_IsRejected()
    {
        var p = WriteRvz("t.rvz", 1000, declaredContainer: 999_999);
        Assert.False(RvzHeader.TryRead(p, out _, out var err));
        Assert.Contains("truncated", err);
    }

    [Fact]
    public void Header_TooSmallOrZeroLogicalSize_IsRejected()
    {
        var small = Path.Combine(_dir, "s.rvz");
        File.WriteAllBytes(small, new byte[10]);
        Assert.False(RvzHeader.TryRead(small, out _, out _));
        Assert.False(RvzHeader.TryRead(WriteRvz("z.rvz", 0), out _, out _));
    }

    [Fact]
    public void RvzIds_MatchSeededRegistryRows()
    {
        Assert.Equal("rvz_compression", RvzIds.TransformId);
        Assert.Equal("dolphintool", RvzIds.ToolId);
        Assert.True(RvzIds.IsRvzTransform(new TransformRecord { Id = "rvz_compression" }));
        Assert.False(RvzIds.IsRvzTransform(new TransformRecord { Id = "chd_dvd_compression" }));
        Assert.False(RvzIds.IsRvzTransform(null));
    }
}
