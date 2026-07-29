using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class DatLineStoreMetadataV2Tests : IDisposable
{
    private readonly string _dbPath;

    public DatLineStoreMetadataV2Tests()
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

    // ── Schema ────────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureSchema_CreatesReleaseMetadataV2Columns()
    {
        Open(); // triggers EnsureSchema
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('release_metadata')";
        using var reader = cmd.ExecuteReader();
        var cols = new HashSet<string>();
        while (reader.Read()) cols.Add(reader.GetString(0));

        Assert.Contains("sort_title",   cols);
        Assert.Contains("genre",        cols);
        Assert.Contains("subgenre",     cols);
        Assert.Contains("players",      cols);
        Assert.Contains("release_type", cols);
        Assert.Contains("rating",       cols);
        Assert.Contains("notes",        cols);
    }

    [Fact]
    public void EnsureSchema_CreatesReleaseMetadataFieldStateTable()
    {
        Open();
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name='release_metadata_field_state'
            """;
        Assert.Equal(1L, (long)(cmd.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void EnsureSchema_CreatesReleaseMetadataProposalsTable()
    {
        Open();
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name='release_metadata_proposals'
            """;
        Assert.Equal(1L, (long)(cmd.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void EnsureSchema_IsIdempotent_SecondOpenDoesNotThrow()
    {
        Open();
        var ex = Record.Exception(() => Open());
        Assert.Null(ex);
    }

    // ── SaveReleaseMetadata / LoadReleaseMetadata — v2 fields ─────────────────

    private static ReleaseMetadataRecord MakeFullRecord(string id = "rel-001") => new()
    {
        ReleaseId       = id,
        Title           = "Animal Basket",
        OriginalTitle   = "アニマルバスケット",
        SortTitle       = "Animal Basket",
        Developer       = "Dev Co",
        Publisher       = "Pub Co",
        Year            = "2003",
        Languages       = "en",
        AlternateTitles = "AB",
        Description     = "A game.",
        Genre           = "Sports",
        Subgenre        = "Basketball",
        Players         = "1-2",
        ReleaseType     = "Retail",
        Rating          = "E",
        Notes           = "Good dump.",
        ScrapedAtUtc    = "2026-01-01T00:00:00Z",
    };

    [Fact]
    public void SaveAndLoad_PreservesAllV2Fields()
    {
        var store = Open();
        var rec   = MakeFullRecord();
        store.SaveReleaseMetadata(rec);

        var loaded = store.LoadReleaseMetadata();
        Assert.True(loaded.TryGetValue("rel-001", out var got));

        Assert.Equal(rec.SortTitle,   got!.SortTitle);
        Assert.Equal(rec.Genre,       got.Genre);
        Assert.Equal(rec.Subgenre,    got.Subgenre);
        Assert.Equal(rec.Players,     got.Players);
        Assert.Equal(rec.ReleaseType, got.ReleaseType);
        Assert.Equal(rec.Rating,      got.Rating);
        Assert.Equal(rec.Notes,       got.Notes);
    }

    [Fact]
    public void SaveAndLoad_PreservesV1Fields()
    {
        var store = Open();
        var rec   = MakeFullRecord();
        store.SaveReleaseMetadata(rec);

        var loaded = store.LoadReleaseMetadata();
        Assert.True(loaded.TryGetValue("rel-001", out var got));

        Assert.Equal(rec.Title,           got!.Title);
        Assert.Equal(rec.OriginalTitle,   got.OriginalTitle);
        Assert.Equal(rec.Developer,       got.Developer);
        Assert.Equal(rec.Publisher,       got.Publisher);
        Assert.Equal(rec.Year,            got.Year);
        Assert.Equal(rec.Languages,       got.Languages);
        Assert.Equal(rec.AlternateTitles, got.AlternateTitles);
        Assert.Equal(rec.Description,     got.Description);
        Assert.Equal(rec.ScrapedAtUtc,    got.ScrapedAtUtc);
    }

    [Fact]
    public void SaveReleaseMetadata_Upsert_OverwritesV2Fields()
    {
        var store = Open();
        store.SaveReleaseMetadata(MakeFullRecord());
        store.SaveReleaseMetadata(new ReleaseMetadataRecord
        {
            ReleaseId    = "rel-001",
            Title        = "Animal Basket",
            Genre        = "Puzzle",
            Subgenre     = "",
            Players      = "1",
            Notes        = "Updated note.",
            ScrapedAtUtc = "2026-01-01T00:00:00Z",
        });

        var loaded = store.LoadReleaseMetadata();
        Assert.Equal("Puzzle",        loaded["rel-001"].Genre);
        Assert.Equal("",              loaded["rel-001"].Subgenre);
        Assert.Equal("1",             loaded["rel-001"].Players);
        Assert.Equal("Updated note.", loaded["rel-001"].Notes);
    }

    [Fact]
    public void LoadReleaseMetadata_ReturnsEmpty_NewFields_WhenNotSaved()
    {
        var store = Open();
        // Save a record with only v1 fields populated (v2 fields default to "")
        store.SaveReleaseMetadata(new ReleaseMetadataRecord
        {
            ReleaseId    = "rel-002",
            Title        = "Test",
            ScrapedAtUtc = "2026-01-01T00:00:00Z",
        });

        var got = store.LoadReleaseMetadata()["rel-002"];
        Assert.Equal("", got.SortTitle);
        Assert.Equal("", got.Genre);
        Assert.Equal("", got.Subgenre);
        Assert.Equal("", got.Players);
        Assert.Equal("", got.ReleaseType);
        Assert.Equal("", got.Rating);
        Assert.Equal("", got.Notes);
    }

    // ── Field state ───────────────────────────────────────────────────────────

    [Fact]
    public void SaveMetadataFieldState_CanBeLoaded()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title", "manual", "", locked: true);

        var states = store.LoadMetadataFieldStates("rel-001");
        var state  = Assert.Single(states);

        Assert.Equal("rel-001", state.ReleaseId);
        Assert.Equal("title",   state.Field);
        Assert.Equal("manual",  state.Source);
        Assert.Equal("",        state.Provider);
        Assert.True(state.Locked);
        Assert.NotEmpty(state.UpdatedAtUtc);
    }

    [Fact]
    public void SaveMetadataFieldState_Upsert_UpdatesExisting()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title", "provider", "screenscraper", locked: false);
        store.SaveMetadataFieldState("rel-001", "title", "manual",   "",              locked: true);

        var states = store.LoadMetadataFieldStates("rel-001");
        var state  = Assert.Single(states);

        Assert.Equal("manual", state.Source);
        Assert.True(state.Locked);
    }

    [Fact]
    public void LoadMetadataFieldStates_ReturnsMultipleFields()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title",       "manual",   "", locked: true);
        store.SaveMetadataFieldState("rel-001", "description", "provider", "screenscraper", locked: false);

        var states = store.LoadMetadataFieldStates("rel-001");
        Assert.Equal(2, states.Count);
    }

    [Fact]
    public void LoadMetadataFieldStates_ReturnsEmpty_WhenNoStates()
    {
        var store  = Open();
        var states = store.LoadMetadataFieldStates("nonexistent");
        Assert.Empty(states);
    }

    [Fact]
    public void IsMetadataFieldLocked_ReturnsTrue_WhenLocked()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title", "manual", "", locked: true);
        Assert.True(store.IsMetadataFieldLocked("rel-001", "title"));
    }

    [Fact]
    public void IsMetadataFieldLocked_ReturnsFalse_WhenUnlocked()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title", "provider", "ss", locked: false);
        Assert.False(store.IsMetadataFieldLocked("rel-001", "title"));
    }

    [Fact]
    public void IsMetadataFieldLocked_ReturnsFalse_WhenFieldAbsent()
    {
        var store = Open();
        Assert.False(store.IsMetadataFieldLocked("rel-001", "title"));
    }

    [Fact]
    public void SetMetadataFieldLocked_SetsLockedFlag()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title", "manual", "", locked: false);
        store.SetMetadataFieldLocked("rel-001", "title", locked: true);
        Assert.True(store.IsMetadataFieldLocked("rel-001", "title"));
    }

    [Fact]
    public void SetMetadataFieldLocked_ClearsLockedFlag()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "title", "manual", "", locked: true);
        store.SetMetadataFieldLocked("rel-001", "title", locked: false);
        Assert.False(store.IsMetadataFieldLocked("rel-001", "title"));
    }

    [Fact]
    public void SetMetadataFieldLocked_InsertsRow_WhenNoneExists()
    {
        var store = Open();
        // No prior SaveMetadataFieldState — SetMetadataFieldLocked must upsert
        store.SetMetadataFieldLocked("rel-001", "title", locked: true);
        Assert.True(store.IsMetadataFieldLocked("rel-001", "title"));
    }

    // ── Proposals ─────────────────────────────────────────────────────────────

    [Fact]
    public void SaveMetadataProposal_CanBeLoaded()
    {
        var store = Open();
        store.SaveMetadataProposal("rel-001", "screenscraper", "title", "Animal Basket");

        var proposals = store.LoadMetadataProposals("rel-001", "screenscraper");
        var p         = Assert.Single(proposals);

        Assert.Equal("rel-001",        p.ReleaseId);
        Assert.Equal("screenscraper",  p.Provider);
        Assert.Equal("title",          p.Field);
        Assert.Equal("Animal Basket",  p.Value);
        Assert.False(p.Accepted);
        Assert.NotEmpty(p.ScrapedAt);
    }

    [Fact]
    public void SaveMetadataProposal_Upsert_UpdatesValueAndResetsAccepted()
    {
        var store = Open();
        store.SaveMetadataProposal("rel-001", "screenscraper", "title", "Old Title");
        store.MarkMetadataProposalAccepted("rel-001", "screenscraper", "title");
        store.SaveMetadataProposal("rel-001", "screenscraper", "title", "New Title");

        var proposals = store.LoadMetadataProposals("rel-001", "screenscraper");
        var p         = Assert.Single(proposals);

        Assert.Equal("New Title", p.Value);
        Assert.False(p.Accepted); // re-scrape resets accepted
    }

    [Fact]
    public void SaveMetadataProposals_BatchInsertsAllFields()
    {
        var store  = Open();
        var fields = new Dictionary<string, string>
        {
            ["title"]       = "Animal Basket",
            ["developer"]   = "Dev Co",
            ["year"]        = "2003",
            ["genre"]       = "Sports",
        };
        store.SaveMetadataProposals("rel-001", "screenscraper", fields);

        var proposals = store.LoadMetadataProposals("rel-001", "screenscraper");
        Assert.Equal(4, proposals.Count);
        Assert.Contains(proposals, p => p.Field == "title"     && p.Value == "Animal Basket");
        Assert.Contains(proposals, p => p.Field == "developer" && p.Value == "Dev Co");
        Assert.Contains(proposals, p => p.Field == "year"      && p.Value == "2003");
        Assert.Contains(proposals, p => p.Field == "genre"     && p.Value == "Sports");
    }

    [Fact]
    public void SaveMetadataProposals_Upsert_UpdatesExistingFieldsInBatch()
    {
        var store = Open();
        store.SaveMetadataProposals("rel-001", "screenscraper",
            new Dictionary<string, string> { ["title"] = "Old" });
        store.SaveMetadataProposals("rel-001", "screenscraper",
            new Dictionary<string, string> { ["title"] = "New", ["year"] = "2005" });

        var proposals = store.LoadMetadataProposals("rel-001", "screenscraper");
        Assert.Equal(2, proposals.Count);
        Assert.Equal("New", proposals.First(p => p.Field == "title").Value);
    }

    [Fact]
    public void LoadMetadataProposals_ReturnsEmpty_WhenNoneExist()
    {
        var store = Open();
        Assert.Empty(store.LoadMetadataProposals("rel-001", "screenscraper"));
    }

    [Fact]
    public void LoadMetadataProposals_IsolatesProviders()
    {
        var store = Open();
        store.SaveMetadataProposal("rel-001", "screenscraper", "title", "From SS");
        store.SaveMetadataProposal("rel-001", "igdb",          "title", "From IGDB");

        var ss   = store.LoadMetadataProposals("rel-001", "screenscraper");
        var igdb = store.LoadMetadataProposals("rel-001", "igdb");

        Assert.Single(ss);
        Assert.Equal("From SS",   ss[0].Value);
        Assert.Single(igdb);
        Assert.Equal("From IGDB", igdb[0].Value);
    }

    [Fact]
    public void LoadAllMetadataProposals_ReturnsBothProviders()
    {
        var store = Open();
        store.SaveMetadataProposal("rel-001", "screenscraper", "title", "SS Title");
        store.SaveMetadataProposal("rel-001", "igdb",          "title", "IGDB Title");
        store.SaveMetadataProposal("rel-001", "screenscraper", "year",  "2003");

        var all = store.LoadAllMetadataProposals("rel-001");
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void LoadAllMetadataProposals_ReturnsEmpty_WhenNoneExist()
    {
        var store = Open();
        Assert.Empty(store.LoadAllMetadataProposals("rel-001"));
    }

    [Fact]
    public void MarkMetadataProposalAccepted_SetsAcceptedFlag()
    {
        var store = Open();
        store.SaveMetadataProposal("rel-001", "screenscraper", "title", "Animal Basket");
        store.MarkMetadataProposalAccepted("rel-001", "screenscraper", "title");

        var proposals = store.LoadMetadataProposals("rel-001", "screenscraper");
        Assert.True(proposals[0].Accepted);
    }

    [Fact]
    public void MarkMetadataProposalAccepted_DoesNotAffectOtherFields()
    {
        var store = Open();
        store.SaveMetadataProposal("rel-001", "screenscraper", "title",  "Animal Basket");
        store.SaveMetadataProposal("rel-001", "screenscraper", "year",   "2003");
        store.MarkMetadataProposalAccepted("rel-001", "screenscraper", "title");

        var proposals = store.LoadMetadataProposals("rel-001", "screenscraper");
        Assert.True(proposals.First(p => p.Field == "title").Accepted);
        Assert.False(proposals.First(p => p.Field == "year").Accepted);
    }

    [Fact]
    public void DeleteMetadataProposals_RemovesAllForProvider()
    {
        var store = Open();
        store.SaveMetadataProposal("rel-001", "screenscraper", "title", "T");
        store.SaveMetadataProposal("rel-001", "screenscraper", "year",  "2003");
        store.SaveMetadataProposal("rel-001", "igdb",          "title", "T2");

        store.DeleteMetadataProposals("rel-001", "screenscraper");

        Assert.Empty(store.LoadMetadataProposals("rel-001", "screenscraper"));
        Assert.Single(store.LoadMetadataProposals("rel-001", "igdb")); // unaffected
    }

    [Fact]
    public void DeleteMetadataProposals_IsNoOp_WhenNoneExist()
    {
        var store = Open();
        var ex = Record.Exception(() => store.DeleteMetadataProposals("rel-001", "screenscraper"));
        Assert.Null(ex);
    }
}
