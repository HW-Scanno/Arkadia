using System;
using System.Collections.Generic;
using System.IO;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

public sealed class DatLineStoreExtraNotesTests : IDisposable
{
    private readonly string _dbPath;

    public DatLineStoreExtraNotesTests()
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

    // ── 1. Returns null/empty for no row ─────────────────────────────────────

    [Fact]
    public void GetReleaseExtraNotes_ReturnsNull_WhenNoRow()
    {
        var notes = Open().GetReleaseExtraNotes("rel-001");
        Assert.Null(notes);
    }

    // ── 2. SaveReleaseExtraNotes stores notes ─────────────────────────────────

    [Fact]
    public void SaveReleaseExtraNotes_StoresAndReturnsNotes()
    {
        var store = Open();
        store.SaveReleaseExtraNotes("rel-001", "My notes here");
        Assert.Equal("My notes here", store.GetReleaseExtraNotes("rel-001"));
    }

    // ── 3. Newlines are preserved ─────────────────────────────────────────────

    [Fact]
    public void SaveReleaseExtraNotes_PreservesNewlines()
    {
        var store = Open();
        var text  = "Line one\nLine two\nLine three";
        store.SaveReleaseExtraNotes("rel-001", text);
        Assert.Equal(text, store.GetReleaseExtraNotes("rel-001"));
    }

    // ── 4. Whitespace-only clears / deletes notes ─────────────────────────────

    [Fact]
    public void SaveReleaseExtraNotes_WhitespaceOnly_DeletesRow()
    {
        var store = Open();
        store.SaveReleaseExtraNotes("rel-001", "Some notes");
        store.SaveReleaseExtraNotes("rel-001", "   \t  ");
        Assert.Null(store.GetReleaseExtraNotes("rel-001"));
    }

    [Fact]
    public void SaveReleaseExtraNotes_EmptyString_DeletesRow()
    {
        var store = Open();
        store.SaveReleaseExtraNotes("rel-001", "Some notes");
        store.SaveReleaseExtraNotes("rel-001", "");
        Assert.Null(store.GetReleaseExtraNotes("rel-001"));
    }

    [Fact]
    public void SaveReleaseExtraNotes_Null_DeletesRow()
    {
        var store = Open();
        store.SaveReleaseExtraNotes("rel-001", "Some notes");
        store.SaveReleaseExtraNotes("rel-001", null);
        Assert.Null(store.GetReleaseExtraNotes("rel-001"));
    }

    // ── 5. Updating preserves one row per release ─────────────────────────────

    [Fact]
    public void SaveReleaseExtraNotes_UpdateKeepsOneRow()
    {
        var store = Open();
        store.SaveReleaseExtraNotes("rel-001", "First version");
        store.SaveReleaseExtraNotes("rel-001", "Second version");
        Assert.Equal("Second version", store.GetReleaseExtraNotes("rel-001"));
    }

    // ── 6. Two releases do not conflict ──────────────────────────────────────

    [Fact]
    public void SaveReleaseExtraNotes_TwoReleases_DontConflict()
    {
        var store = Open();
        store.SaveReleaseExtraNotes("rel-001", "Notes for A");
        store.SaveReleaseExtraNotes("rel-002", "Notes for B");

        Assert.Equal("Notes for A", store.GetReleaseExtraNotes("rel-001"));
        Assert.Equal("Notes for B", store.GetReleaseExtraNotes("rel-002"));
    }

    // ── 7. Metadata merge does not alter extra notes ──────────────────────────

    [Fact]
    public void ApplyMergeSelections_DoesNotAlterExtraNotes()
    {
        var store = Open();
        // Seed a release with metadata and extra notes
        store.SaveReleaseMetadata(new ReleaseMetadataRecord { ReleaseId = "rel-001", Title = "Old Title" });
        store.SaveReleaseExtraNotes("rel-001", "My curated notes");

        // Simulate a merge-dialog apply
        var all     = store.LoadReleaseMetadata();
        var current = all.GetValueOrDefault("rel-001") ?? new ReleaseMetadataRecord { ReleaseId = "rel-001" };
        store.ApplyMergeSelections(
            "rel-001", "screenscraper",
            [("title", "New Title")],
            current);

        // Extra notes must be untouched
        Assert.Equal("My curated notes", store.GetReleaseExtraNotes("rel-001"));
    }

    // ── 8. Provider proposals do not alter extra notes ────────────────────────

    [Fact]
    public void ApplyProviderProposals_DoesNotAlterExtraNotes()
    {
        var store = Open();
        store.SaveReleaseMetadata(new ReleaseMetadataRecord { ReleaseId = "rel-001" });
        store.SaveReleaseExtraNotes("rel-001", "Protected notes");

        var all     = store.LoadReleaseMetadata();
        var current = all.GetValueOrDefault("rel-001") ?? new ReleaseMetadataRecord { ReleaseId = "rel-001" };
        store.ApplyProviderProposals(
            "rel-001", "screenscraper",
            new Dictionary<string, string> { ["title"] = "Proposed Title" },
            current,
            autoApplyEmptyFields: true);

        Assert.Equal("Protected notes", store.GetReleaseExtraNotes("rel-001"));
    }

    // ── 9. Notes are not stored in release_provider_payloads ─────────────────

    [Fact]
    public void ExtraNotes_NotInProviderPayloads()
    {
        var store = Open();
        store.SaveReleaseExtraNotes("rel-001", "Some notes");

        // Provider payloads table should have no row for this release
        var payload = store.LoadProviderPayload("rel-001", "screenscraper");
        Assert.Null(payload);
    }

    // ── 10. Notes are not provider proposals ─────────────────────────────────

    [Fact]
    public void ExtraNotes_NotInMetadataProposals()
    {
        var store = Open();
        store.SaveReleaseExtraNotes("rel-001", "Some notes");

        // Proposal table should have no entry for "extra_notes" field
        var proposals = store.LoadMetadataProposals("rel-001", "screenscraper");
        Assert.DoesNotContain(proposals, p => p.Field == "extra_notes");
    }

    // ── 11. created_at preserved on update ────────────────────────────────────

    [Fact]
    public void SaveReleaseExtraNotes_PreservesCreatedAt_OnUpdate()
    {
        var store = Open();
        store.SaveReleaseExtraNotes("rel-001", "First");

        // Read created_at directly
        using var conn1 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn1.Open();
        using var cmd1 = conn1.CreateCommand();
        cmd1.CommandText = "SELECT created_at FROM release_extra_notes WHERE release_id='rel-001'";
        var createdAt1 = (string)cmd1.ExecuteScalar()!;

        store.SaveReleaseExtraNotes("rel-001", "Updated");

        using var conn2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn2.Open();
        using var cmd2 = conn2.CreateCommand();
        cmd2.CommandText = "SELECT created_at FROM release_extra_notes WHERE release_id='rel-001'";
        var createdAt2 = (string)cmd2.ExecuteScalar()!;

        Assert.Equal(createdAt1, createdAt2);
    }

    // ── 12. Schema migration is idempotent ────────────────────────────────────

    [Fact]
    public void OpeningStoreTwice_DoesNotThrow()
    {
        Open();
        Open(); // second open triggers EnsureSchema again — must not throw
    }

    // ── 13. Table exists after construction ───────────────────────────────────

    [Fact]
    public void ReleaseExtraNotesTable_ExistsAfterConstruction()
    {
        Open();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name='release_extra_notes'
            """;
        var count = (long)(cmd.ExecuteScalar() ?? 0L);
        Assert.Equal(1, count);
    }
}
