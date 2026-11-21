using System;
using System.IO;
using System.Security.Cryptography;
using Arkadia.Data;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Phase A — verified ISO → RVZ (RVZ-SUPPORT-01). The DolphinTool convert/verify steps are injected; every test runs
/// in an isolated temp app root. Covers: private workdir, hardlinked (never copied) input, structural + logical
/// verification against the SOURCE ISO, native-rename promotion, no overwrite, and source preservation on failure.
/// </summary>
public sealed class RvzConversionTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _dest;
    private readonly RvzExpectedLogical _expected;
    private static readonly TransformRecord Xform = new()
    {
        Id = "rvz_compression", Name = "RVZ Compression", ToolId = "dolphintool",
        CommandTemplate = "convert -f rvz -b 131072 -c zstd -l 5 -i \"{input}\" -o \"{output}\"", OutputExtension = ".rvz",
    };

    public RvzConversionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ArkRvzConv_" + Guid.NewGuid().ToString("N")[..8]);
        var srcDir = Path.Combine(_root, "source", "gc", "leaf", "Game (USA)");
        Directory.CreateDirectory(srcDir);
        _source = Path.Combine(srcDir, "Game (USA).iso");
        var iso = new byte[4096];
        new Random(7).NextBytes(iso);
        File.WriteAllBytes(_source, iso);
        _dest = Path.Combine(_root, "archive", "gc", "leaf", "Game (USA).rvz");
        _expected = new RvzExpectedLogical(iso.Length,
            Convert.ToHexString(SHA1.HashData(iso)).ToLowerInvariant(),
            Convert.ToHexString(MD5.HashData(iso)).ToLowerInvariant(),
            Crc32Hasher.ComputeHex(new MemoryStream(iso)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void WriteFakeRvz(string path, long logicalSize, int payload = 512)
    {
        var bytes = new byte[RvzHeader.HeaderSize + payload];
        RvzHeader.BuildForTests(logicalSize, bytes.Length).CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
    }

    private DolphinVerifyResult Reconstructs(RvzExpectedLogical e) =>
        new(true, new RvzLogicalHashes(e.Sha1, e.Md5, e.Crc32), "", false, Array.Empty<string>());

    private string WorkRoot => Path.Combine(_root, "transform-work", "rvz");

    [Fact]
    public void Verified_Output_IsPromotedByNativeRename_WorkdirRemoved_SourceUntouched()
    {
        var before = File.ReadAllBytes(_source);
        (uint, ulong)? outputId = null;
        string? convertInput = null, convertOutput = null;

        var r = RvzConversion.Run(_root, Xform, null, _source, _dest, _expected,
            convertOverride: (i, o) =>
            {
                convertInput = i; convertOutput = o;
                WriteFakeRvz(o, _expected.Size);
                outputId = FileIdentity.Of(o);
                return (true, "");
            },
            verifyOverride: _ => Reconstructs(_expected));

        Assert.True(r.Success, r.Error);
        Assert.True(File.Exists(_dest));
        Assert.Equal(outputId, FileIdentity.Of(_dest));                          // rename, not copy
        Assert.StartsWith(WorkRoot, convertOutput!, StringComparison.OrdinalIgnoreCase);   // private workdir output
        Assert.False(Directory.Exists(r.WorkdirUsed));
        Assert.Equal(before, File.ReadAllBytes(_source));                        // source never modified
        Assert.Equal(_expected.Sha1, r.Verified!.Sha1);
        if (r.InputHardlinked) Assert.StartsWith(WorkRoot, convertInput!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HardlinkUnavailable_ToolReadsSourceDirectly_NoCopyIsMade()
    {
        string? convertInput = null;
        var r = RvzConversion.Run(_root, Xform, null, _source, _dest, _expected,
            hardlinkAttempt: (_, _) => false,
            convertOverride: (i, o) =>
            {
                convertInput = i;
                Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(o)!));   // nothing copied into workdir
                WriteFakeRvz(o, _expected.Size);
                return (true, "");
            },
            verifyOverride: _ => Reconstructs(_expected));

        Assert.True(r.Success, r.Error);
        Assert.Equal(_source, convertInput);
        Assert.False(r.InputHardlinked);
    }

    [Fact]
    public void ConversionFailure_NoArchiveFile_SourcePreserved_WorkdirCleaned()
    {
        var r = RvzConversion.Run(_root, Xform, null, _source, _dest, _expected,
            convertOverride: (_, o) => { File.WriteAllBytes(o, new byte[10]); return (false, "Block size must be set"); },
            verifyOverride: _ => throw new InvalidOperationException("verify must not run"));

        Assert.False(r.Success);
        Assert.Contains("Block size must be set", r.Error);
        Assert.False(File.Exists(_dest));
        Assert.True(File.Exists(_source));
        Assert.False(Directory.Exists(r.WorkdirUsed));
    }

    [Fact]
    public void LogicalMismatch_ScrubbedOutput_IsRejected_NothingPromoted()
    {
        var scrubbed = _expected with { Sha1 = new string('f', 40) };
        var r = RvzConversion.Run(_root, Xform, null, _source, _dest, _expected,
            convertOverride: (_, o) => { WriteFakeRvz(o, _expected.Size); return (true, ""); },
            verifyOverride: _ => Reconstructs(scrubbed));

        Assert.False(r.Success);
        Assert.Contains("does not reconstruct", r.Error);
        Assert.False(File.Exists(_dest));
        Assert.True(File.Exists(_source));
    }

    [Fact]
    public void Md5OrCrcMismatch_IsRejected()
    {
        foreach (var bad in new[] { _expected with { Md5 = new string('0', 32) }, _expected with { Crc32 = "00000000" } })
        {
            var r = RvzConversion.Run(_root, Xform, null, _source, _dest, _expected,
                convertOverride: (_, o) => { WriteFakeRvz(o, _expected.Size); return (true, ""); },
                verifyOverride: _ => Reconstructs(bad));
            Assert.False(r.Success);
            Assert.False(File.Exists(_dest));
        }
    }

    [Fact]
    public void VerifyToolFailure_IsRejected_NothingPromoted()
    {
        var r = RvzConversion.Run(_root, Xform, null, _source, _dest, _expected,
            convertOverride: (_, o) => { WriteFakeRvz(o, _expected.Size); return (true, ""); },
            verifyOverride: _ => DolphinVerifyResult.Fail("exit code 1"));

        Assert.False(r.Success);
        Assert.Contains("could not run", r.Error);
        Assert.False(File.Exists(_dest));
    }

    [Fact]
    public void WrongLogicalSize_OrNonRvzOutput_IsRejected_BeforeVerify()
    {
        var r1 = RvzConversion.Run(_root, Xform, null, _source, _dest, _expected,
            convertOverride: (_, o) => { WriteFakeRvz(o, _expected.Size + 1); return (true, ""); },
            verifyOverride: _ => throw new InvalidOperationException("verify must not run"));
        Assert.False(r1.Success);
        Assert.Contains("logical size", r1.Error);

        var r2 = RvzConversion.Run(_root, Xform, null, _source, _dest, _expected,
            convertOverride: (_, o) => { File.WriteAllBytes(o, new byte[200]); return (true, ""); },
            verifyOverride: _ => throw new InvalidOperationException("verify must not run"));
        Assert.False(r2.Success);
        Assert.False(File.Exists(_dest));
    }

    [Fact]
    public void ExistingArchiveTarget_IsNeverOverwritten_ToolNotRun()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dest)!);
        File.WriteAllText(_dest, "existing");
        var r = RvzConversion.Run(_root, Xform, null, _source, _dest, _expected,
            convertOverride: (_, _) => throw new InvalidOperationException("convert must not run"),
            verifyOverride: _ => throw new InvalidOperationException("verify must not run"));

        Assert.False(r.Success);
        Assert.Equal("existing", File.ReadAllText(_dest));
    }

    [Fact]
    public void MissingTool_FailsCleanly_SourcePreserved()
    {
        var tool = new ToolRecord { Id = "dolphintool", FolderName = "dolphintool", ExecutableName = "DolphinTool.exe" };
        var r = RvzConversion.Run(_root, Xform, tool, _source, _dest, _expected);
        Assert.False(r.Success);
        Assert.Contains("not found", r.Error);
        Assert.False(File.Exists(_dest));
        Assert.True(File.Exists(_source));
    }

    [Fact]
    public void Compare_RequiresSha1_AndChecksDeclaredMd5Crc()
    {
        var l = new RvzLogicalHashes(_expected.Sha1, _expected.Md5, _expected.Crc32);
        Assert.Null(RvzConversion.Compare(_expected, l));
        Assert.Null(RvzConversion.Compare(_expected with { Md5 = "", Crc32 = "" }, l));
        Assert.NotNull(RvzConversion.Compare(_expected with { Sha1 = "" }, l));     // never accept without SHA1
    }

    [Fact]
    public void Compare_LogicalMd5NotComputed_StillRequiresSha1AndCrc()
    {
        var noMd5 = new RvzLogicalHashes(_expected.Sha1, "", _expected.Crc32);
        Assert.Null(RvzConversion.Compare(_expected, noMd5));
        Assert.NotNull(RvzConversion.Compare(_expected, noMd5 with { Sha1 = new string('0', 40) }));
        Assert.NotNull(RvzConversion.Compare(_expected, noMd5 with { Crc32 = "00000000" }));
    }
}
