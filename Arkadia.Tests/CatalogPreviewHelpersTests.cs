using System;
using System.IO;
using Xunit;

namespace Arkadia.Tests;

public sealed class CatalogPreviewHelpersTests : IDisposable
{
    private readonly string _tmpDir;

    public CatalogPreviewHelpersTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    // ── I. Non-existent file returns null ─────────────────────────────────────

    [Fact]
    public void TryLoadBitmap_NonExistentFile_ReturnsNull()
    {
        var missing = Path.Combine(_tmpDir, "no-such-file.png");

        var result = CatalogPreviewHelpers.TryLoadBitmap(missing);

        Assert.Null(result);
    }

    // ── J. Corrupt / non-image file returns null ──────────────────────────────

    [Fact]
    public void TryLoadBitmap_CorruptFile_ReturnsNull()
    {
        var corrupt = Path.Combine(_tmpDir, "corrupt.png");
        File.WriteAllBytes(corrupt, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var result = CatalogPreviewHelpers.TryLoadBitmap(corrupt);

        Assert.Null(result);
    }
}
