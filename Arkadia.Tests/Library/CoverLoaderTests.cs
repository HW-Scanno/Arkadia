using System;
using System.IO;
using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Library;

/// <summary>
/// Tests for CoverLoader.IsValidFile — the file-level validation gate
/// that runs before any Bitmap is created.
/// Covers all four scenarios required by the media-loading hardening spec:
/// .php rejected, zero-byte rejected, corrupt bytes accepted at file level
/// (Bitmap decode failure is caught by TryLoad), valid PNG bytes accepted.
/// </summary>
public sealed class CoverLoaderTests : IDisposable
{
    private readonly string _dir;

    public CoverLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ── Extension guard ───────────────────────────────────────────────────────

    [Fact]
    public void PhpFile_IsRejected()
    {
        var path = Write("cover.php", [0xFF, 0xD8, 0xFF]);   // JPEG magic, but wrong ext
        Assert.False(CoverLoader.IsValidFile(path));
    }

    [Theory]
    [InlineData("cover.tmp")]
    [InlineData("cover.html")]
    [InlineData("cover.xml")]
    [InlineData("cover.json")]
    [InlineData("cover.bmp")]
    [InlineData("cover.gif")]
    [InlineData("cover.tiff")]
    public void NonImageExtension_IsRejected(string filename)
    {
        var path = Write(filename, [0x01, 0x02, 0x03, 0x04]);
        Assert.False(CoverLoader.IsValidFile(path));
    }

    // ── Size guard ────────────────────────────────────────────────────────────

    [Fact]
    public void ZeroByteImage_IsRejected()
    {
        var path = Write("cover.png", []);
        Assert.False(CoverLoader.IsValidFile(path));
    }

    [Fact]
    public void ZeroByteJpeg_IsRejected()
    {
        var path = Write("cover.jpg", []);
        Assert.False(CoverLoader.IsValidFile(path));
    }

    // ── Corrupt content ───────────────────────────────────────────────────────

    [Fact]
    public void CorruptImage_PassesFileValidation()
    {
        // Garbage bytes with a valid extension — file-level validation passes.
        // TryLoad will then attempt Bitmap decode; the try/catch ensures no crash.
        var path = Write("corrupt.png", [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03]);
        Assert.True(CoverLoader.IsValidFile(path));
    }

    [Fact]
    public void CorruptJpeg_PassesFileValidation()
    {
        var path = Write("corrupt.jpg", [0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF]);
        Assert.True(CoverLoader.IsValidFile(path));
    }

    // ── Valid content ─────────────────────────────────────────────────────────

    [Fact]
    public void ValidPng_MagicBytes_PassesFileValidation()
    {
        // PNG magic: 89 50 4E 47 0D 0A 1A 0A
        var path = Write("valid.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Assert.True(CoverLoader.IsValidFile(path));
    }

    [Fact]
    public void ValidJpeg_MagicBytes_PassesFileValidation()
    {
        // JPEG magic: FF D8 FF
        var path = Write("valid.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);
        Assert.True(CoverLoader.IsValidFile(path));
    }

    [Fact]
    public void ValidWebp_MagicBytes_PassesFileValidation()
    {
        // RIFF....WEBP
        var path = Write("valid.webp", [
            0x52, 0x49, 0x46, 0x46,  // RIFF
            0x24, 0x00, 0x00, 0x00,  // size (ignored)
            0x57, 0x45, 0x42, 0x50,  // WEBP
        ]);
        Assert.True(CoverLoader.IsValidFile(path));
    }

    // ── Null / missing ────────────────────────────────────────────────────────

    [Fact]
    public void NullPath_IsRejected()
        => Assert.False(CoverLoader.IsValidFile(null));

    [Fact]
    public void EmptyPath_IsRejected()
        => Assert.False(CoverLoader.IsValidFile(""));

    [Fact]
    public void MissingFile_IsRejected()
        => Assert.False(CoverLoader.IsValidFile(Path.Combine(_dir, "does_not_exist.png")));
}
