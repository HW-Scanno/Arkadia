using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>
/// Byte-level tests for the shared ingestion container-extraction seam <see cref="ArchiveExtractor"/> (policy A:
/// bundled 7-Zip preferred, managed <c>System.IO.Compression</c> ZIP as fallback). CI-safe managed-fallback tests
/// always run; the 7-Zip-preferred tests are gated on a resolvable bundled/PATH 7-Zip (they no-op when absent).
/// </summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _tmp;

    public ArchiveExtractorTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ArkExtract_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { } }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string NewDir(string name) { var d = Path.Combine(_tmp, name); Directory.CreateDirectory(d); return d; }

    /// <summary>Creates a managed (Deflate) ZIP with the given entryName→content map.</summary>
    private string MakeDeflateZip(string fileName, params (string Name, byte[] Content)[] entries)
    {
        var zip = Path.Combine(_tmp, fileName);
        using var fs = File.Create(zip);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var e = za.CreateEntry(name, CompressionLevel.Optimal);   // Deflate
            using var s = e.Open();
            s.Write(content, 0, content.Length);
        }
        return zip;
    }

    /// <summary>Locates a usable 7-Zip: the production discovery, then the deployed bundle, then PATH. Null ⇒ gate off.</summary>
    private static string? Find7z()
    {
        var prod = Arkadia.ProviderHelpers.Find7zip();   // AppContext.BaseDirectory/tools/7zip/7zip.exe
        if (prod is not null) return prod;
        var candidates = new[]
        {
            @"G:\Arkadia\tools\7zip\7zip.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { "7z.exe", "7za.exe" })
                try { var f = Path.Combine(dir, name); if (File.Exists(f)) return f; } catch { }
        return null;
    }

    /// <summary>Creates a ZIP with a specific 7-Zip compression method (e.g. PPMd) via the located 7-Zip.</summary>
    private string Make7zMethodZip(string sevenZip, string fileName, string method, byte[] content)
    {
        var payload = Path.Combine(_tmp, "payload_" + Guid.NewGuid().ToString("N")[..6] + ".bin");
        File.WriteAllBytes(payload, content);
        var zip = Path.Combine(_tmp, fileName);
        var psi = new ProcessStartInfo(sevenZip) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("a"); psi.ArgumentList.Add("-tzip"); psi.ArgumentList.Add($"-mm={method}");
        psi.ArgumentList.Add(zip); psi.ArgumentList.Add(payload);
        using var p = Process.Start(psi)!; p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return File.Exists(zip) ? zip : throw new InvalidOperationException("7-Zip could not create the fixture");
    }

    // ── B/F: managed fallback extracts ordinary ZIPs ─────────────────────────

    [Fact]
    public void Managed_DeflateZip_ExtractsWhenNo7Zip()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("hello deflate");
        var zip = MakeDeflateZip("m.zip", ("a.txt", content));
        var dest = NewDir("m_out");

        ArchiveExtractor.Extract(zip, dest, sevenZipPath: null);   // fallback path

        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void Managed_MultiEntry_FullyExtracted()
    {
        var zip = MakeDeflateZip("multi.zip",
            ("one.txt",       System.Text.Encoding.UTF8.GetBytes("1")),
            ("sub/two.txt",   System.Text.Encoding.UTF8.GetBytes("22")),
            ("sub/three.bin", new byte[] { 3, 3, 3 }));
        var dest = NewDir("multi_out");

        ArchiveExtractor.Extract(zip, dest, sevenZipPath: null);

        Assert.True(File.Exists(Path.Combine(dest, "one.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "two.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "three.bin")));
        Assert.Equal(3, Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length);
    }

    // ── C: non-ZIP with no 7-Zip throws (caller pre-flight skips this, but the seam is explicit) ──

    [Fact]
    public void Managed_NonZip_NoSevenZip_Throws()
    {
        var fake7z = Path.Combine(_tmp, "x.7z");
        File.WriteAllBytes(fake7z, new byte[] { 1, 2, 3 });
        var dest = NewDir("nz_out");
        var ex = Assert.Throws<InvalidOperationException>(() => ArchiveExtractor.Extract(fake7z, dest, sevenZipPath: null));
        Assert.Contains("7zip is required", ex.Message);
    }

    // ── D (managed): corrupt ZIP throws (caller maps to extract-failed, incoming intact) ──

    [Fact]
    public void Managed_CorruptZip_Throws()
    {
        var bad = Path.Combine(_tmp, "bad.zip");
        File.WriteAllBytes(bad, System.Text.Encoding.ASCII.GetBytes("PK not really a zip"));
        var dest = NewDir("bad_out");
        Assert.ThrowsAny<Exception>(() => ArchiveExtractor.Extract(bad, dest, sevenZipPath: null));
    }

    // ── E (managed): a ../ traversal entry is contained (skipped) — nothing outside dest ──

    [Fact]
    public void Managed_TraversalEntry_Contained()
    {
        var zip = Path.Combine(_tmp, "evil_m.zip");
        using (var fs = File.Create(zip))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var s = za.CreateEntry("../ESCAPED.txt").Open()) s.Write(new byte[] { 9 }, 0, 1);
            using (var s = za.CreateEntry("good.txt").Open())       s.Write(new byte[] { 1 }, 0, 1);
        }
        var parent = NewDir("evil_m_parent");
        var dest   = Path.Combine(parent, "out");   // extract here; ../ would land in parent

        ArchiveExtractor.Extract(zip, dest, sevenZipPath: null);

        Assert.False(File.Exists(Path.Combine(parent, "ESCAPED.txt")), "traversal entry escaped the dest root");
        Assert.True(File.Exists(Path.Combine(dest, "good.txt")));
    }

    // ── §14: the ZIP metadata size-probe reads uncompressed total independently of extraction ──

    [Fact]
    public void MetadataSizeProbe_ReturnsUncompressedTotal()
    {
        var c1 = new byte[1000]; var c2 = new byte[2345];
        var zip = MakeDeflateZip("probe.zip", ("a.bin", c1), ("b.bin", c2));
        using var za = ZipFile.OpenRead(zip);
        Assert.Equal(1000 + 2345, za.Entries.Sum(e => e.Length));   // same probe used by RunPreIngest, engine-agnostic
    }

    // ── A: 7-Zip PREFERRED for an ordinary ZIP (gated) ──

    [Fact]
    public void SevenZip_Preferred_DeflateZip_Extracts()
    {
        var sevenZip = Find7z();
        if (sevenZip is null) return;   // environment-gated (no bundled/PATH 7-Zip)

        var content = System.Text.Encoding.UTF8.GetBytes("via seven zip");
        var zip  = MakeDeflateZip("s.zip", ("a.txt", content));
        var dest = NewDir("s_out");

        ArchiveExtractor.Extract(zip, dest, sevenZip);   // preferred path

        Assert.True(File.Exists(Path.Combine(dest, "a.txt")));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(dest, "a.txt")));
    }

    // ── Routing proof + "managed can't decode, 7-Zip can" (Zstd surrogate via PPMd) (gated) ──

    [Fact]
    public void SevenZip_Preferred_ExtractsMethodManagedCannotDecode()
    {
        var sevenZip = Find7z();
        if (sevenZip is null) return;

        var content = System.Text.Encoding.UTF8.GetBytes(new string('Z', 20000));
        string zip;
        try { zip = Make7zMethodZip(sevenZip, "ppmd.zip", "PPMd", content); }
        catch { return; }   // this 7-Zip build can't create the method — skip

        // Managed extractor cannot decode PPMd (stands in for Zstd): it throws.
        var mDest = NewDir("ppmd_managed");
        Assert.ThrowsAny<Exception>(() => ArchiveExtractor.Extract(zip, mDest, sevenZipPath: null));

        // Preferred 7-Zip decodes it → proves routing to 7-Zip, not silent managed fallback.
        var sDest = NewDir("ppmd_7z");
        ArchiveExtractor.Extract(zip, sDest, sevenZip);
        Assert.Single(Directory.GetFiles(sDest, "*", SearchOption.AllDirectories));
    }

    // ── E (7-Zip): traversal contained by the bundled 7-Zip (gated) ──

    [Fact]
    public void SevenZip_TraversalEntry_Contained()
    {
        var sevenZip = Find7z();
        if (sevenZip is null) return;

        var zip = Path.Combine(_tmp, "evil_s.zip");
        using (var fs = File.Create(zip))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var s = za.CreateEntry("../ESCAPED.txt").Open()) s.Write(new byte[] { 9 }, 0, 1);
            using (var s = za.CreateEntry("good.txt").Open())       s.Write(new byte[] { 1 }, 0, 1);
        }
        var parent = NewDir("evil_s_parent");
        var dest   = Path.Combine(parent, "out");

        ArchiveExtractor.Extract(zip, dest, sevenZip);

        Assert.False(File.Exists(Path.Combine(parent, "ESCAPED.txt")), "7-Zip let a traversal entry escape the dest root");
        Assert.True(Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length >= 1);
    }

    // ── D (7-Zip): corrupt archive via 7-Zip throws (gated) ──

    [Fact]
    public void SevenZip_CorruptArchive_Throws()
    {
        var sevenZip = Find7z();
        if (sevenZip is null) return;

        var bad = Path.Combine(_tmp, "bad_s.zip");
        File.WriteAllBytes(bad, System.Text.Encoding.ASCII.GetBytes("PK totally not a zip payload"));
        var dest = NewDir("bad_s_out");
        Assert.ThrowsAny<Exception>(() => ArchiveExtractor.Extract(bad, dest, sevenZip));
    }
}
