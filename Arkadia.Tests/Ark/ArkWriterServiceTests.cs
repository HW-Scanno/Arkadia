using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Arkadia;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Ark;

public sealed class ArkWriterServiceTests : IDisposable
{
    private readonly string         _baseDir;
    private readonly CatalogService _catalog;
    private readonly List<string>   _tempExtracted = [];

    private const string HwFamilyId = "snes";
    private const string DatLineId  = "snes-nointro";
    private const string ReleaseId  = "rel-001";

    public ArkWriterServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _catalog = new CatalogService(_baseDir);
    }

    public void Dispose()
    {
        foreach (var p in _tempExtracted)
            try { File.Delete(p); } catch { }
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArkWriterService Svc() => new(_baseDir, _catalog);

    private string OutputPath() =>
        Path.Combine(_baseDir, "output", "test.ark");

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

    private string ExtractZipEntry(ZipArchive zip, string entryPath)
    {
        var entry    = zip.GetEntry(entryPath)!;
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        _tempExtracted.Add(tempPath);
        using var src  = entry.Open();
        using var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
        src.CopyTo(dest);
        return tempPath;
    }

    private SqliteConnection OpenExtractedDb(ZipArchive zip, string entryPath)
    {
        var path = ExtractZipEntry(zip, entryPath);
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        return conn;
    }

    private static long QueryCount(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static bool HasSettingKey(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    // ── 1. Creates .ark file ──────────────────────────────────────────────────

    [Fact]
    public void Write_CreatesArkFile()
    {
        var result = Svc().Write(new ArkExportOptions(), OutputPath());

        Assert.True(result.Success);
        Assert.True(File.Exists(result.OutputPath));
    }

    // ── 2. Manifest present, correct version ─────────────────────────────────

    [Fact]
    public void Write_ManifestPresent_CorrectVersion()
    {
        var result = Svc().Write(new ArkExportOptions(), OutputPath());

        using var zip     = ZipFile.OpenRead(result.OutputPath);
        var       entry   = zip.GetEntry("manifest.json");
        Assert.NotNull(entry);

        using var stream  = entry.Open();
        using var doc     = JsonDocument.Parse(stream);
        var       root    = doc.RootElement;

        Assert.Equal("Arkadia Backup", root.GetProperty("FormatName").GetString());
        Assert.Equal("0.5",            root.GetProperty("FormatVersion").GetString());
    }

    // ── 3. Hash manifest present, all entries listed (not itself) ─────────────

    [Fact]
    public void Write_HashManifestPresent_AllEntriesListed()
    {
        var result = Svc().Write(new ArkExportOptions(), OutputPath());

        using var zip = ZipFile.OpenRead(result.OutputPath);

        var hashEntry = zip.GetEntry("hashes/files.sha256.json");
        Assert.NotNull(hashEntry);

        // Deserialize hash list
        List<ArkFileHashEntry> hashes;
        using (var s = hashEntry.Open())
            hashes = JsonSerializer.Deserialize<List<ArkFileHashEntry>>(s, new JsonSerializerOptions())!;

        var hashedPaths = hashes.Select(h => h.Path).ToHashSet(StringComparer.Ordinal);

        // Every entry except the hash file itself must appear in the hash list
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == "hashes/files.sha256.json") continue;
            Assert.Contains(entry.FullName, hashedPaths);
        }

        // The hash file must not list itself
        Assert.DoesNotContain("hashes/files.sha256.json", hashedPaths);
    }

    // ── 4. catalog.db present and readable ───────────────────────────────────

    [Fact]
    public void Write_CatalogDbPresent()
    {
        var result = Svc().Write(new ArkExportOptions(), OutputPath());

        using var zip  = ZipFile.OpenRead(result.OutputPath);
        Assert.NotNull(zip.GetEntry("db/catalog.db"));

        using var conn = OpenExtractedDb(zip, "db/catalog.db");
        // Can query settings table → DB is readable
        var count = QueryCount(conn, "settings");
        Assert.True(count >= 0);
    }

    // ── 5. DAT-line DBs present and readable ─────────────────────────────────

    [Fact]
    public void Write_DatLineDbsPresent()
    {
        RegisterDatLine();

        var result       = Svc().Write(new ArkExportOptions(), OutputPath());
        var archivePath  = $"db/systems/{HwFamilyId}/{DatLineId}.db";

        using var zip  = ZipFile.OpenRead(result.OutputPath);
        Assert.NotNull(zip.GetEntry(archivePath));

        using var conn = OpenExtractedDb(zip, archivePath);
        var count      = QueryCount(conn, "releases");
        Assert.True(count >= 0);
    }

    // ── 6. Credentials absent from exported catalog.db ───────────────────────

    [Fact]
    public void Write_CredentialsNotInExportedCatalogDb()
    {
        _catalog.SetSetting("screenscraper_username",     "myuser");
        _catalog.SetSetting("screenscraper_password",     "mypass");
        _catalog.SetSetting("screenscraper_dev_id",       "devid42");
        _catalog.SetSetting("screenscraper_dev_password", "devpass");
        _catalog.SetSetting("screenscraper_softname",     "myapp");

        var result = Svc().Write(new ArkExportOptions(), OutputPath());

        using var zip  = ZipFile.OpenRead(result.OutputPath);
        using var conn = OpenExtractedDb(zip, "db/catalog.db");

        Assert.False(HasSettingKey(conn, "screenscraper_username"));
        Assert.False(HasSettingKey(conn, "screenscraper_password"));
        Assert.False(HasSettingKey(conn, "screenscraper_dev_id"));
        Assert.False(HasSettingKey(conn, "screenscraper_dev_password"));
        Assert.False(HasSettingKey(conn, "screenscraper_softname"));
    }

    // ── 7. Cache package tables empty in exported catalog.db ─────────────────

    [Fact]
    public void Write_CachePackageTablesEmptyInExportedCatalogDb()
    {
        // Insert a cache_packages row directly (FK off — no package file needed)
        var catalogDbPath = Path.Combine(_baseDir, "catalog.db");
        using (var rawConn = new SqliteConnection($"Data Source={catalogDbPath}"))
        {
            rawConn.Open();
            using var fkOff = rawConn.CreateCommand();
            fkOff.CommandText = "PRAGMA foreign_keys = OFF";
            fkOff.ExecuteNonQuery();
            using var ins = rawConn.CreateCommand();
            ins.CommandText = """
                INSERT INTO cache_packages(package_path, provider, cache_provider_id, system_id, system_name, built_at_utc, indexed_at_utc)
                VALUES('/path/cache.zip', 'screenscraper', 'ss-1', 'snes', 'SNES', '2026-01-01', '2026-01-01')
                """;
            ins.ExecuteNonQuery();
        }

        var result = Svc().Write(new ArkExportOptions(), OutputPath());

        using var zip  = ZipFile.OpenRead(result.OutputPath);
        using var conn = OpenExtractedDb(zip, "db/catalog.db");

        Assert.Equal(0, QueryCount(conn, "cache_packages"));
        Assert.Equal(0, QueryCount(conn, "cache_package_games"));
        Assert.Equal(0, QueryCount(conn, "cache_package_media"));
        Assert.Equal(0, QueryCount(conn, "cache_package_search_terms"));
    }

    // ── 8. Provider payloads absent from exported DAT DB ─────────────────────

    [Fact]
    public void Write_ProviderPayloadsEmptyInExportedDatDb()
    {
        var store = RegisterDatLine();
        store.SaveReleases([new ReleaseRecord
        {
            Id = ReleaseId, DatLineId = DatLineId,
            Name = "Super Mario World (USA)", Status = "present",
        }]);
        store.SaveProviderPayload(ReleaseId, "screenscraper", "{\"id\":\"42\"}");

        var result      = Svc().Write(new ArkExportOptions(), OutputPath());
        var archivePath = $"db/systems/{HwFamilyId}/{DatLineId}.db";

        using var zip  = ZipFile.OpenRead(result.OutputPath);
        using var conn = OpenExtractedDb(zip, archivePath);

        Assert.Equal(0, QueryCount(conn, "release_provider_payloads"));
    }

    // ── 9. Pending reconciliations absent from exported DAT DB ───────────────

    [Fact]
    public void Write_PendingReconciliationsEmptyInExportedDatDb()
    {
        var store = RegisterDatLine();
        store.SavePendingReconciliation(new PendingReconciliationRecord
        {
            Id               = "recon-1",
            NewReleaseId     = "rel-new",
            OutdatedReleaseId = "rel-old",
            TargetName       = "new-name.zip",
            Reason           = "content_hash_match",
            CreatedAtUtc     = DateTime.UtcNow,
            Status           = "pending",
        });

        var result      = Svc().Write(new ArkExportOptions(), OutputPath());
        var archivePath = $"db/systems/{HwFamilyId}/{DatLineId}.db";

        using var zip  = ZipFile.OpenRead(result.OutputPath);
        using var conn = OpenExtractedDb(zip, archivePath);

        Assert.Equal(0, QueryCount(conn, "pending_reconciliations"));
    }

    // ── 10. Sidecar SHA-256 file created ─────────────────────────────────────

    [Fact]
    public void Write_SidecarSha256FileCreated()
    {
        var result = Svc().Write(new ArkExportOptions(), OutputPath());

        Assert.True(File.Exists(result.SidecarPath));

        var content = File.ReadAllText(result.SidecarPath).Trim();
        var parts   = content.Split("  ", 2);

        Assert.Equal(2,  parts.Length);
        Assert.Equal(64, parts[0].Length);
        Assert.Matches("^[0-9a-f]{64}$", parts[0]);
        Assert.Equal(Path.GetFileName(result.OutputPath), parts[1].Trim());
        Assert.Equal(result.Sha256, parts[0]);
    }

    // ── 11. Overwrite=false throws when file exists ───────────────────────────

    [Fact]
    public void Write_OverwriteFalseExistingFileThrows()
    {
        var outPath = OutputPath();
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, "placeholder");

        Assert.Throws<InvalidOperationException>(
            () => Svc().Write(new ArkExportOptions(), outPath, overwrite: false));
    }

    // ── 12. Tmp file cleaned on failure ──────────────────────────────────────

    [Fact]
    public void Write_TmpCleanedOnFailure()
    {
        // A pre-cancelled token is checked inside the ZIP block — after the
        // tmpPath FileStream is opened — so tmpPath is created then cleaned up.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outPath = OutputPath();
        var tmpPath = outPath + ".tmp";

        Assert.Throws<OperationCanceledException>(
            () => Svc().Write(new ArkExportOptions(), outPath, ct: cts.Token));

        Assert.False(File.Exists(tmpPath));
    }
}
