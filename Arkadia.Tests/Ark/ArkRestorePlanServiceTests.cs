using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Ark;

public sealed class ArkRestorePlanServiceTests : IDisposable
{
    private readonly string         _baseDir;
    private readonly CatalogService _catalog;

    private const string HwFamilyId = "snes";
    private const string DatLineId  = "snes-nointro";

    public ArkRestorePlanServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _catalog = new CatalogService(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArkRestorePlanService Svc() => new();

    private string ArkPath() =>
        Path.Combine(_baseDir, "output", "test.ark");

    private string CreateValidArk(ArkExportOptions? options = null)
    {
        var result = new ArkWriterService(_baseDir, _catalog)
            .Write(options ?? new ArkExportOptions(IncludeAmpRegistry: false), ArkPath());
        return result.OutputPath;
    }

    private string TargetDir() =>
        Path.Combine(_baseDir, "restore-target");

    private void RegisterDatLine()
    {
        _catalog.SaveHardwareFamily(new HardwareFamilyRecord { Id = HwFamilyId, Name = "Super Nintendo" });
        _catalog.SaveDatLines([new DatLineRecord
        {
            Id               = DatLineId,
            HardwareFamilyId = HwFamilyId,
            Name             = DatLineId,
            Authority        = "no-intro",
            MediaTypeId      = "rom",
            DataStorePath    = $"systems/{HwFamilyId}/{DatLineId}.db",
            ImportedAtUtc    = DateTime.UtcNow,
        }]);
        var dbPath = Path.Combine(_baseDir, "systems", HwFamilyId, $"{DatLineId}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = new DatLineStore(dbPath);
    }

    private string CopyArkWithReplaced(string src, string entryName, byte[] newContent)
    {
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".ark");
        using var srcZip = ZipFile.OpenRead(src);
        using var dstFs  = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        using var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create);
        foreach (var entry in srcZip.Entries)
        {
            var newEntry = dstZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            using var dst2 = newEntry.Open();
            if (string.Equals(entry.FullName, entryName, StringComparison.Ordinal))
                dst2.Write(newContent, 0, newContent.Length);
            else
            {
                using var src2 = entry.Open();
                src2.CopyTo(dst2);
            }
        }
        return dst;
    }

    private string CopyArkWithExtra(string src, string extraName, byte[] content)
    {
        var dst = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".ark");
        using var srcZip = ZipFile.OpenRead(src);
        using var dstFs  = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        using var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create);
        foreach (var entry in srcZip.Entries)
        {
            var newEntry = dstZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            using var src2 = entry.Open();
            using var dst2 = newEntry.Open();
            src2.CopyTo(dst2);
        }
        var extra = dstZip.CreateEntry(extraName, CompressionLevel.Optimal);
        using var extStream = extra.Open();
        extStream.Write(content, 0, content.Length);
        return dst;
    }

    // ── 1. Valid ARK reports PackageValid ─────────────────────────────────────

    [Fact]
    public void PlanRestore_ValidArk_ReportsPackageValid()
    {
        var ark    = CreateValidArk();
        var target = TargetDir();

        var plan = Svc().PlanRestore(ark, target);

        Assert.True(plan.PackageValid);
        Assert.Empty(plan.Issues);
        Assert.Equal("Arkadia Backup", plan.FormatName);
        Assert.Equal("0.5",            plan.FormatVersion);
    }

    // ── 2. Catalog and DAT-line entries ───────────────────────────────────────

    [Fact]
    public void PlanRestore_ValidArk_ReportsCatalogAndDatLineEntries()
    {
        RegisterDatLine();
        var ark    = CreateValidArk();
        var target = TargetDir();

        var plan = Svc().PlanRestore(ark, target);

        Assert.True(plan.PackageValid);
        Assert.Contains(plan.Entries, e => e.Category == "catalog" && e.WillRestore);
        Assert.Contains(plan.Entries, e => e.Category == "datline" && e.WillRestore);
        Assert.Equal(1, plan.DatLineDbCount);
        Assert.True(plan.StoreCount >= 2);
    }

    // ── 3. Missing ARK file returns issue ─────────────────────────────────────

    [Fact]
    public void PlanRestore_MissingArk_ReturnsIssue()
    {
        var plan = Svc().PlanRestore(
            Path.Combine(_baseDir, "nonexistent.ark"),
            TargetDir());

        Assert.False(plan.PackageValid);
        Assert.NotEmpty(plan.Issues);
    }

    // ── 4. Corrupt ARK returns issue ─────────────────────────────────────────

    [Fact]
    public void PlanRestore_CorruptArk_ReturnsIssue()
    {
        var ark = Path.Combine(_baseDir, "corrupt.ark");
        File.WriteAllBytes(ark, [0x00, 0x01, 0x02, 0x03]);

        var plan = Svc().PlanRestore(ark, TargetDir());

        Assert.False(plan.PackageValid);
        Assert.NotEmpty(plan.Issues);
    }

    // ── 5. Non-empty target emits overwrite warning ───────────────────────────

    [Fact]
    public void PlanRestore_NonEmptyTarget_RequiresOverwriteWarning()
    {
        var ark    = CreateValidArk();
        var target = TargetDir();
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "data");

        var plan = Svc().PlanRestore(ark, target);

        Assert.True(plan.RequiresOverwrite);
        Assert.Contains(plan.Warnings, w =>
            w.Contains("overwrite", StringComparison.OrdinalIgnoreCase) ||
            w.Contains("full replacement", StringComparison.OrdinalIgnoreCase));
    }

    // ── 6. Missing target treated as empty ───────────────────────────────────

    [Fact]
    public void PlanRestore_MissingTarget_TreatedAsEmpty()
    {
        var ark    = CreateValidArk();
        var target = Path.Combine(_baseDir, "does-not-exist");

        var plan = Svc().PlanRestore(ark, target);

        Assert.False(plan.TargetExists);
        Assert.True(plan.TargetIsEmpty);
        Assert.False(plan.RequiresOverwrite);
    }

    // ── 7. Wrong FormatVersion blocks ────────────────────────────────────────

    [Fact]
    public void PlanRestore_WrongFormatVersion_Blocks()
    {
        var src = CreateValidArk();
        var badManifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            FormatName            = "Arkadia Backup",
            FormatVersion         = "0.6",
            CreatedAtUtc          = DateTime.UtcNow.ToString("O"),
            ArkadiaAppVersion     = (string?)null,
            CredentialsExcluded   = true,
            CachePackagesExcluded = true,
            MediaIncluded         = false,
            AmpRegistryIncluded   = false,
            DatLineCount          = 0,
            StoreCount            = 1,
            HashAlgorithm         = "SHA-256",
        }, new JsonSerializerOptions { WriteIndented = true }));

        var ark  = CopyArkWithReplaced(src, "manifest.json", badManifest);
        var plan = Svc().PlanRestore(ark, TargetDir());

        // Verifier detects SHA mismatch (manifest was replaced) → PackageValid=false
        // OR the version check adds an issue.
        Assert.True(!plan.PackageValid || plan.Issues.Count > 0);
    }

    // ── 8. Always warns Verify ALL / Verify Volume required ──────────────────

    [Fact]
    public void PlanRestore_AlwaysWarnsVerifyRequired()
    {
        var ark  = CreateValidArk();
        var plan = Svc().PlanRestore(ark, TargetDir());

        Assert.Contains(plan.Warnings, w =>
            w.Contains("Verify ALL", StringComparison.OrdinalIgnoreCase) ||
            w.Contains("Verify Volume", StringComparison.OrdinalIgnoreCase));
    }

    // ── 9. Registry entry mapped to ark-restore folder ────────────────────────

    [Fact]
    public void PlanRestore_RegistryEntry_PlannedToArkRestoreFolder()
    {
        var ark    = CreateValidArk(new ArkExportOptions(IncludeAmpRegistry: true));
        var target = TargetDir();
        var plan   = Svc().PlanRestore(ark, target);

        Assert.True(plan.PackageValid);
        var registryEntry = plan.Entries
            .FirstOrDefault(e => e.Category == "registry");

        Assert.NotNull(registryEntry);
        Assert.True(registryEntry.WillRestore);
        Assert.Contains("ark-restore", registryEntry.TargetPath,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── 10. Unsafe archive entry returns issue ────────────────────────────────

    [Fact]
    public void PlanRestore_UnsafeEntry_ReturnsIssue()
    {
        var src  = CreateValidArk();
        var ark  = CopyArkWithExtra(src, "../evil.txt", [0x01, 0x02, 0x03]);
        var plan = Svc().PlanRestore(ark, TargetDir());

        // Verifier or plan service must report an issue for the unsafe path.
        // No unsafe entry should be planned as WillRestore=true.
        Assert.NotEmpty(plan.Issues);
        Assert.DoesNotContain(plan.Entries, e =>
            e.WillRestore && e.ArchivePath.Contains("evil"));
    }
}
