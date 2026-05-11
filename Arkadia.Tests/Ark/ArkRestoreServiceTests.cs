using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Ark;

public sealed class ArkRestoreServiceTests : IDisposable
{
    private readonly string         _baseDir;
    private readonly CatalogService _catalog;

    private const string HwFamilyId = "snes";
    private const string DatLineId  = "snes-nointro";

    public ArkRestoreServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _catalog = new CatalogService(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArkRestoreService Svc() => new();

    private string CreateArk(ArkExportOptions? opts = null)
    {
        var outPath = Path.Combine(_baseDir, "output", Guid.NewGuid().ToString("N") + ".ark");
        return new ArkWriterService(_baseDir, _catalog)
            .Write(opts ?? new ArkExportOptions(IncludeAmpRegistry: false), outPath)
            .OutputPath;
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

    // ── 1. Restore into missing target ────────────────────────────────────────

    [Fact]
    public void Restore_IntoMissingTarget_Succeeds()
    {
        var ark    = CreateArk();
        var target = TargetDir();

        var result = Svc().Restore(ark, target);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(target, "catalog.db")));
        Assert.Null(result.PreviousDataBackupDir);
        Assert.False(result.OverwriteUsed);
    }

    // ── 2. Restore into empty target ─────────────────────────────────────────

    [Fact]
    public void Restore_IntoEmptyTarget_Succeeds()
    {
        var ark    = CreateArk();
        var target = TargetDir();
        Directory.CreateDirectory(target);

        var result = Svc().Restore(ark, target);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(target, "catalog.db")));
    }

    // ── 3. Non-empty target + overwrite=false throws ──────────────────────────

    [Fact]
    public void Restore_NonEmptyTarget_OverwriteFalse_Throws()
    {
        var ark    = CreateArk();
        var target = TargetDir();
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "data");

        Assert.Throws<InvalidOperationException>(
            () => Svc().Restore(ark, target, overwrite: false));

        Assert.True(File.Exists(Path.Combine(target, "existing.txt")));
    }

    // ── 4. Non-empty target + overwrite=true moves previous aside ─────────────

    [Fact]
    public void Restore_NonEmptyTarget_OverwriteTrue_MovesPreviousAside()
    {
        var ark    = CreateArk();
        var target = TargetDir();
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "old data");

        var result = Svc().Restore(ark, target, overwrite: true);

        Assert.True(result.Success);
        Assert.True(result.OverwriteUsed);
        Assert.NotNull(result.PreviousDataBackupDir);
        Assert.True(Directory.Exists(result.PreviousDataBackupDir));
        Assert.True(File.Exists(Path.Combine(result.PreviousDataBackupDir, "existing.txt")));
        Assert.True(File.Exists(Path.Combine(target, "catalog.db")));
    }

    // ── 5. Invalid (corrupt) ARK throws and does not create target ────────────

    [Fact]
    public void Restore_InvalidArk_ThrowsAndDoesNotCreateTarget()
    {
        var ark    = Path.Combine(_baseDir, "corrupt.ark");
        File.WriteAllBytes(ark, [0x00, 0x01, 0x02, 0x03]);
        var target = TargetDir();

        Assert.Throws<InvalidOperationException>(
            () => Svc().Restore(ark, target));

        Assert.False(Directory.Exists(target));
    }

    // ── 6. Path-traversal ARK throws and leaves target unchanged ─────────────

    [Fact]
    public void Restore_PathTraversalArk_Throws()
    {
        var ark        = CreateArk();
        var mutated    = CopyArkWithExtra(ark, "../evil.txt", [0x01, 0x02, 0x03]);
        var target     = TargetDir();

        Assert.Throws<InvalidOperationException>(
            () => Svc().Restore(mutated, target));

        Assert.False(Directory.Exists(target));
    }

    // ── 7. DAT-line DB restored ───────────────────────────────────────────────

    [Fact]
    public void Restore_RestoresDatLineDb()
    {
        RegisterDatLine();
        var ark    = CreateArk();
        var target = TargetDir();

        var result = Svc().Restore(ark, target);

        Assert.True(result.Success);
        Assert.True(File.Exists(
            Path.Combine(target, "systems", HwFamilyId, $"{DatLineId}.db")));
    }

    // ── 8. Registry entry restored to ark-restore folder ─────────────────────

    [Fact]
    public void Restore_RestoresRegistryManifestToArkRestoreFolder()
    {
        var ark    = CreateArk(new ArkExportOptions(IncludeAmpRegistry: true));
        var target = TargetDir();

        var result = Svc().Restore(ark, target);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(target, "ark-restore", "amp-packages.json")));
    }

    // ── 9. Result contains post-restore Verify warning ───────────────────────

    [Fact]
    public void Restore_ResultContainsVerifyWarning()
    {
        var ark    = CreateArk();
        var result = Svc().Restore(ark, TargetDir());

        Assert.Contains(result.Warnings, w =>
            w.Contains("Verify ALL", StringComparison.OrdinalIgnoreCase) ||
            w.Contains("Verify Volume", StringComparison.OrdinalIgnoreCase));
    }

    // ── 10. Staging cleaned up on cancellation ────────────────────────────────

    [Fact]
    public void Restore_StagingCleanedOnCancellation()
    {
        var ark    = CreateArk();
        var target = TargetDir();
        var parent = Directory.GetParent(Path.GetFullPath(target))!.FullName;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Svc().Restore(ark, target, ct: cts.Token));

        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetDirectories(parent, ".ark-restore-*"));
    }
}
