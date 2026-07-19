using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Arkadia.Data;
using Arkadia.LocalArchive;
using Xunit;

namespace Arkadia.Tests.Archive;

/// <summary>
/// M1d readers-unchanged tests: Verify Archive still classifies BOTH the new flat
/// layout and legacy release-foldered layout, and the reader contract (resolve via
/// derived_artifacts.relative_path) works for both. Uses the real, unmodified
/// LocalArchiveVerifyService.
/// </summary>
public sealed class ArchiveLayoutReaderCompatibilityTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _appRoot;
    private readonly string _dbPath;
    private const string P = "dc";
    private const string D = "redump";

    public ArchiveLayoutReaderCompatibilityTests()
    {
        _tmp     = Path.Combine(Path.GetTempPath(), "ArkM1dRead_" + Guid.NewGuid().ToString("N")[..8]);
        _appRoot = Path.Combine(_tmp, "approot");
        _dbPath  = Path.Combine(_tmp, "dat.db");
        Directory.CreateDirectory(_appRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private DatLineStore Store() => new(_dbPath);

    private static string Sha1(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();

    /// <summary>Provisions a wanted derived artifact with an explicit relative_path and writes its file.</summary>
    private string ProvisionAt(string relativePath, string fileName, byte[] content)
    {
        var sha1  = Sha1(content);
        var cik   = $"sha1:{sha1}";
        var relId = Guid.NewGuid().ToString("N");
        var store = Store();
        store.UpsertRelease(new ReleaseRecord { Id = relId, DatLineId = D, Name = "Rel " + fileName, Status = "present" });
        store.EnsureContentIdentity(new ContentIdentityRecord
        { ContentIdentityKey = cik, DatSha1 = sha1, DatMd5 = null, DatCrc32 = null, CreatedAtUtc = DateTime.UtcNow });
        store.SaveReleaseContentLink(new ReleaseContentLinkRecord
        { Id = Guid.NewGuid().ToString("N"), ReleaseId = relId, ContentIdentityKey = cik, CreatedAtUtc = DateTime.UtcNow });
        store.IngestDerivedArtifact(cik, "", "chd", fileName, relativePath, content.Length, sha1);

        var full = Path.Combine(_appRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return cik;
    }

    // ── 14 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void VerifyArchive_StillClassifiesFlatAndFolderedLayouts()
    {
        var flatContent     = System.Text.Encoding.UTF8.GetBytes("flat-chd-content");
        var folderedContent = System.Text.Encoding.UTF8.GetBytes("legacy-foldered-content");

        ProvisionAt($"archive/{P}/{D}/New Game (USA).chd", "New Game (USA).chd", flatContent);
        ProvisionAt($"archive/{P}/{D}/Old Release/legacy.chd", "legacy.chd", folderedContent);

        var plan = new LocalArchiveVerifyService(_appRoot).Verify(P, D, Store());

        // Both physical files found by the recursive scan and classified OK (hash match).
        Assert.Equal(2, plan.FilesScanned);
        Assert.All(plan.Entries, e => Assert.Equal(LocalArchiveClass.WantedArchiveOk, e.Classification));
        Assert.Contains(plan.Entries, e => e.FileName == "New Game (USA).chd");
        Assert.Contains(plan.Entries, e => e.FileName == "legacy.chd");
    }

    // ── 15 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AppendBuildRepairReaders_StillUseRelativePath()
    {
        // Readers (Append/Build/Repair) resolve archive sources as
        // Path.Combine(appRoot, da.RelativePath) — this works for both layouts.
        var flat     = System.Text.Encoding.UTF8.GetBytes("flat");
        var foldered = System.Text.Encoding.UTF8.GetBytes("foldered");
        ProvisionAt($"archive/{P}/{D}/Flat.chd", "Flat.chd", flat);
        ProvisionAt($"archive/{P}/{D}/Rel/Foldered.chd", "Foldered.chd", foldered);

        var das = Store().GetDerivedArtifacts();
        Assert.Equal(2, das.Count);

        foreach (var da in das)
        {
            var readerPath = Path.Combine(_appRoot, da.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(readerPath), $"reader must resolve {da.RelativePath} via relative_path");
        }
    }
}
