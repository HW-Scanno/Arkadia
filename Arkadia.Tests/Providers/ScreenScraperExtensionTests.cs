using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Providers;

/// <summary>
/// Tests for ScreenScraperClient.ResolveExtension — the three-tier extension
/// detection pipeline (JSON hint → Content-Type → magic bytes).
/// </summary>
public sealed class ScreenScraperExtensionTests : IDisposable
{
    private readonly string _tmp;

    public ScreenScraperExtensionTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string WriteTmp(string name, byte[] content)
    {
        var path = Path.Combine(_tmp, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ── Tier 1: JSON hint ─────────────────────────────────────────────────────

    [Fact]
    public void Hint_mp4_ResolvesAsVideo()
    {
        var p = WriteTmp("a.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("mp4", "", p, ScreenScraperClient.ValidVideoExts);
        Assert.Equal(".mp4", ext);
    }

    [Fact]
    public void Hint_jpg_ResolvesAsImage()
    {
        var p = WriteTmp("b.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("jpg", "", p, ScreenScraperClient.ValidImageExts);
        Assert.Equal(".jpg", ext);
    }

    [Fact]
    public void Hint_php_IsRejected_FallsThroughToContentType()
    {
        var p = WriteTmp("c.tmp", [0x00]);
        // "php" is not in ValidVideoExts → falls to content-type "video/mp4"
        var ext = ScreenScraperClient.ResolveExtension("php", "video/mp4", p, ScreenScraperClient.ValidVideoExts);
        Assert.Equal(".mp4", ext);
    }

    [Fact]
    public void Hint_WithDotPrefix_IsAccepted()
    {
        var p = WriteTmp("d.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension(".png", "", p, ScreenScraperClient.ValidImageExts);
        Assert.Equal(".png", ext);
    }

    // ── Tier 2: Content-Type ──────────────────────────────────────────────────

    [Fact]
    public void NoHint_ContentTypeVideoMp4_ResolvesAsMp4()
    {
        var p = WriteTmp("e.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "video/mp4", p, ScreenScraperClient.ValidVideoExts);
        Assert.Equal(".mp4", ext);
    }

    [Fact]
    public void NoHint_ContentTypeImagePng_ResolvesAsPng()
    {
        var p = WriteTmp("f.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "image/png", p, ScreenScraperClient.ValidImageExts);
        Assert.Equal(".png", ext);
    }

    [Fact]
    public void NoHint_ContentTypeImageJpeg_ResolvesAsJpg()
    {
        var p = WriteTmp("g.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "image/jpeg", p, ScreenScraperClient.ValidImageExts);
        Assert.Equal(".jpg", ext);
    }

    [Fact]
    public void NoHint_ContentTypeImageWebp_ResolvesAsWebp()
    {
        var p = WriteTmp("h.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "image/webp", p, ScreenScraperClient.ValidImageExts);
        Assert.Equal(".webp", ext);
    }

    [Fact]
    public void NoHint_ContentTypeVideoWebm_ResolvesAsWebm()
    {
        var p = WriteTmp("i.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "video/webm", p, ScreenScraperClient.ValidVideoExts);
        Assert.Equal(".webm", ext);
    }

    [Fact]
    public void NoHint_ContentTypeVideoMp4_WithImageValidExts_ReturnsNull()
    {
        // Content-Type is video but caller only accepts images — should return null
        var p = WriteTmp("j.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "video/mp4", p, ScreenScraperClient.ValidImageExts);
        Assert.Null(ext);
    }

    // ── Tier 3: Magic bytes ───────────────────────────────────────────────────

    [Fact]
    public void NoHintNoContentType_PngMagicBytes_ResolvesAsPng()
    {
        // PNG magic: 89 50 4E 47
        var p = WriteTmp("k.tmp", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ext = ScreenScraperClient.ResolveExtension("", "", p, ScreenScraperClient.ValidImageExts);
        Assert.Equal(".png", ext);
    }

    [Fact]
    public void NoHintNoContentType_JpegMagicBytes_ResolvesAsJpg()
    {
        // JPEG magic: FF D8 FF
        var p = WriteTmp("l.tmp", [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
        var ext = ScreenScraperClient.ResolveExtension("", "", p, ScreenScraperClient.ValidImageExts);
        Assert.Equal(".jpg", ext);
    }

    [Fact]
    public void NoHintNoContentType_Mp4MagicBytes_ResolvesAsMp4()
    {
        // MP4: ftyp at offset 4 (66 74 79 70)
        var p = WriteTmp("m.tmp", [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D]);
        var ext = ScreenScraperClient.ResolveExtension("", "", p, ScreenScraperClient.ValidVideoExts);
        Assert.Equal(".mp4", ext);
    }

    [Fact]
    public void NoHintNoContentType_WebmMagicBytes_ResolvesAsWebm()
    {
        // EBML magic: 1A 45 DF A3
        var p = WriteTmp("n.tmp", [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x00, 0x00, 0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "", p, ScreenScraperClient.ValidVideoExts);
        Assert.Equal(".webm", ext);
    }

    // ── PHP URL scenario (user-specified) ─────────────────────────────────────

    [Fact]
    public void PhpUrl_VideoMp4ContentType_SavesAsMp4()
    {
        // Simulates: URL ends in .php, JSON hint absent, Content-Type is video/mp4
        var p = WriteTmp("o.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "video/mp4", p, ScreenScraperClient.ValidVideoExts);
        Assert.Equal(".mp4", ext);
    }

    [Fact]
    public void PhpUrl_ImagePngContentType_SavesAsPng()
    {
        // Simulates: URL ends in .php, JSON hint absent, Content-Type is image/png
        var p = WriteTmp("p.tmp", [0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "image/png", p, ScreenScraperClient.ValidImageExts);
        Assert.Equal(".png", ext);
    }

    [Fact]
    public void PhpUrl_UnknownContent_IsSkipped()
    {
        // Simulates: URL ends in .php, no hint, unknown content type, no recognisable magic
        var p = WriteTmp("q.tmp", [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        var ext = ScreenScraperClient.ResolveExtension("", "", p, ScreenScraperClient.ValidVideoExts);
        Assert.Null(ext);
    }
}
