using System;
using System.IO;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class DatLineStoreTests : IDisposable
{
    private readonly string _dbPath;

    public DatLineStoreTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "test.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }

    private DatLineStore Open() => new(_dbPath);

    // ── release_provider_payloads — schema ────────────────────────────────────

    [Fact]
    public void ProviderPayloadTable_ExistsAfterConstruction()
    {
        // Opening the store is sufficient — EnsureSchema runs in the constructor.
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        Open(); // create DB
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name='release_provider_payloads'
            """;
        var count = (long)(cmd.ExecuteScalar() ?? 0L);
        Assert.Equal(1, count);
    }

    // ── SaveProviderPayload / LoadProviderPayload ─────────────────────────────

    [Fact]
    public void SaveProviderPayload_InsertsNewRow()
    {
        var store = Open();
        store.SaveProviderPayload("rel-001", "screenscraper", """{"players":"1"}""");

        var payload = store.LoadProviderPayload("rel-001", "screenscraper");
        Assert.Equal("""{"players":"1"}""", payload);
    }

    [Fact]
    public void SaveProviderPayload_UpdatesExistingRow_SameKey()
    {
        var store = Open();
        store.SaveProviderPayload("rel-001", "screenscraper", """{"players":"1"}""");
        store.SaveProviderPayload("rel-001", "screenscraper", """{"players":"2","score":"1800"}""");

        var payload = store.LoadProviderPayload("rel-001", "screenscraper");
        Assert.Equal("""{"players":"2","score":"1800"}""", payload);
    }

    [Fact]
    public void SaveProviderPayload_StoresDifferentProvidersSeparately()
    {
        var store = Open();
        store.SaveProviderPayload("rel-001", "screenscraper", """{"source":"ss"}""");
        store.SaveProviderPayload("rel-001", "igdb",          """{"source":"igdb"}""");

        Assert.Equal("""{"source":"ss"}""",   store.LoadProviderPayload("rel-001", "screenscraper"));
        Assert.Equal("""{"source":"igdb"}""", store.LoadProviderPayload("rel-001", "igdb"));
    }

    [Fact]
    public void SaveProviderPayload_StoresDifferentReleasesSeparately()
    {
        var store = Open();
        store.SaveProviderPayload("rel-001", "screenscraper", """{"id":"1"}""");
        store.SaveProviderPayload("rel-002", "screenscraper", """{"id":"2"}""");

        Assert.Equal("""{"id":"1"}""", store.LoadProviderPayload("rel-001", "screenscraper"));
        Assert.Equal("""{"id":"2"}""", store.LoadProviderPayload("rel-002", "screenscraper"));
    }

    [Fact]
    public void LoadProviderPayload_ReturnsNull_WhenNotFound()
    {
        var store = Open();
        Assert.Null(store.LoadProviderPayload("nonexistent", "screenscraper"));
    }

    [Fact]
    public void LoadProviderPayload_ReturnsNull_WhenProviderDiffers()
    {
        var store = Open();
        store.SaveProviderPayload("rel-001", "screenscraper", "{}");

        Assert.Null(store.LoadProviderPayload("rel-001", "igdb"));
    }

    [Fact]
    public void SaveProviderPayload_UpdatesScrapedAt_OnConflict()
    {
        var store = Open();
        store.SaveProviderPayload("rel-001", "screenscraper", "{}");

        // Read the first scraped_at
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        string ReadScrapedAt()
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT scraped_at FROM release_provider_payloads WHERE release_id='rel-001'";
            return cmd.ExecuteScalar() as string ?? "";
        }

        var first = ReadScrapedAt();
        System.Threading.Thread.Sleep(10); // ensure timestamp advances
        store.SaveProviderPayload("rel-001", "screenscraper", """{"updated":true}""");
        var second = ReadScrapedAt();

        // Both are ISO-8601; second must be >= first
        Assert.True(string.Compare(second, first, StringComparison.Ordinal) >= 0);
    }
}
