using System;
using System.IO;
using System.Linq;
using Arkadia;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Ark;

public sealed class ArkExportPlanServiceTests : IDisposable
{
    private readonly string         _baseDir;
    private readonly CatalogService _catalog;

    private const string HwFamilyId = "snes";
    private const string DatLineId  = "snes-nointro";
    private const string ReleaseId  = "rel-001";

    public ArkExportPlanServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _catalog = new CatalogService(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArkExportPlanService Svc() => new(_baseDir, _catalog);

    private DatLineStore RegisterDatLine(string datLineId = DatLineId)
    {
        _catalog.SaveHardwareFamily(new HardwareFamilyRecord { Id = HwFamilyId, Name = "Super Nintendo" });
        _catalog.SaveDatLines([new DatLineRecord
        {
            Id               = datLineId,
            HardwareFamilyId = HwFamilyId,
            Name             = datLineId,
            Authority        = "no-intro",
            MediaTypeId      = "rom",
            DataStorePath    = $"systems/{HwFamilyId}/{datLineId}.db",
            ImportedAtUtc    = DateTime.UtcNow,
        }]);
        var dbPath = Path.Combine(_baseDir, "systems", HwFamilyId, $"{datLineId}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return new DatLineStore(dbPath);
    }

    // ── 1. Store count ────────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_WithCatalogAndDatLines_ReturnsCorrectStoreCount()
    {
        RegisterDatLine("snes-nointro");
        RegisterDatLine("snes-tosec");

        var plan = Svc().PlanExport(new ArkExportOptions());

        var catalogStores = plan.Stores.Where(s => s.Category == "catalog").ToList();
        var datlineStores = plan.Stores.Where(s => s.Category == "datline").ToList();
        Assert.Single(catalogStores);
        Assert.Equal(2, datlineStores.Count);
        Assert.Equal(3, plan.Stores.Count);
        Assert.Equal(2, plan.DatLineCount);

        Assert.Equal("db/catalog.db",                            catalogStores[0].ArchivePath);
        Assert.Contains(datlineStores,
            s => s.ArchivePath == $"db/systems/{HwFamilyId}/snes-nointro.db");
        Assert.Contains(datlineStores,
            s => s.ArchivePath == $"db/systems/{HwFamilyId}/snes-tosec.db");
    }

    // ── 2. Credentials always excluded ────────────────────────────────────────

    [Fact]
    public void PlanExport_CredentialsAlwaysExcluded()
    {
        var plan = Svc().PlanExport(new ArkExportOptions());

        Assert.True(plan.CredentialsExcluded);
        Assert.Contains(plan.Warnings, w => w.Contains("Credentials") || w.Contains("credentials"));
    }

    // ── 3. Cache packages always excluded ─────────────────────────────────────

    [Fact]
    public void PlanExport_CachePackagesAlwaysExcluded()
    {
        var plan = Svc().PlanExport(new ArkExportOptions());

        Assert.True(plan.CachePackagesExcluded);
        Assert.Contains(plan.Warnings, w => w.Contains("Cache packages") || w.Contains("cache packages"));
    }

    // ── 4. Media opt-out ──────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_MediaOptOut_NoMediaBytes()
    {
        var mediaDir = Path.Combine(_baseDir, "media", HwFamilyId, DatLineId, "covers-front");
        Directory.CreateDirectory(mediaDir);
        File.WriteAllBytes(Path.Combine(mediaDir, "cover.png"), [0x89, 0x50, 0x4E, 0x47]);

        var plan = Svc().PlanExport(new ArkExportOptions(IncludeMedia: false));

        Assert.False(plan.MediaIncluded);
        Assert.Equal(0L, plan.MediaEstimatedBytes);
    }

    // ── 5. Media opt-in ───────────────────────────────────────────────────────

    [Fact]
    public void PlanExport_MediaOptIn_MediaBytesPopulated()
    {
        var mediaDir = Path.Combine(_baseDir, "media", HwFamilyId, DatLineId, "covers-front");
        Directory.CreateDirectory(mediaDir);
        var content = new byte[512];
        new Random(42).NextBytes(content);
        File.WriteAllBytes(Path.Combine(mediaDir, "cover.png"), content);

        var plan = Svc().PlanExport(new ArkExportOptions(IncludeMedia: true));

        Assert.True(plan.MediaIncluded);
        Assert.Equal(512L, plan.MediaEstimatedBytes);
        Assert.True(plan.EstimatedUncompressedBytes >= 512L);
    }

    // ── 6. AMP registry opt-in ────────────────────────────────────────────────

    [Fact]
    public void PlanExport_AmpRegistryOptIn_CountsPackages()
    {
        var ampDir = Path.Combine(
            _baseDir,
            ArkadiaFolders.ScrapeCache,
            ArkadiaFolders.ArkadiaMediaPacks);
        Directory.CreateDirectory(ampDir);
        File.WriteAllBytes(Path.Combine(ampDir, "pack-a.amp"), [0x50, 0x4B, 0x03, 0x04]);
        File.WriteAllBytes(Path.Combine(ampDir, "pack-b.amp"), [0x50, 0x4B, 0x03, 0x04]);

        var plan = Svc().PlanExport(new ArkExportOptions(IncludeAmpRegistry: true));

        Assert.True(plan.AmpRegistryIncluded);
        Assert.Equal(2, plan.AmpPackageCount);
    }

    // ── 7. volume_locations absolute path warning ─────────────────────────────

    [Fact]
    public void PlanExport_VolumeLocationsPresent_EmitsAbsolutePathWarning()
    {
        // Insert a volume_locations row with a non-empty path via raw connection
        // (foreign keys off by default so no volume row needed)
        var catalogDbPath = Path.Combine(_baseDir, "catalog.db");
        using (var conn = new SqliteConnection($"Data Source={catalogDbPath}"))
        {
            conn.Open();
            using var fkCmd = conn.CreateCommand();
            fkCmd.CommandText = "PRAGMA foreign_keys = OFF";
            fkCmd.ExecuteNonQuery();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO volume_locations (id, volume_id, location_type, is_current, created_at, path)
                VALUES ('loc-1', 'vol-1', 'managed', 1, '2026-01-01T00:00:00Z', 'C:/roms/snes')
                """;
            cmd.ExecuteNonQuery();
        }

        var plan = Svc().PlanExport(new ArkExportOptions());

        Assert.Contains(plan.Warnings, w => w.Contains("volume_locations") && w.Contains("absolute path"));
    }

    // ── 8. release_media_curation absolute path warning ───────────────────────

    [Fact]
    public void PlanExport_MediaCurationPresent_EmitsAbsolutePathWarning()
    {
        var store = RegisterDatLine();
        store.SaveReleases([new ReleaseRecord
        {
            Id        = ReleaseId,
            DatLineId = DatLineId,
            Name      = "Super Mario World (USA)",
            Status    = "present",
        }]);
        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "cover-front",
            FilePath:       @"C:\media\snes\cover.png",
            FileSha256:     null,
            IsPreferred:    false,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        var plan = Svc().PlanExport(new ArkExportOptions());

        Assert.Contains(plan.Warnings, w =>
            w.Contains("release_media_curation") && w.Contains("absolute path"));
    }
}
