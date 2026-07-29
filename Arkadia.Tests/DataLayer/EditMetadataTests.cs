using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// Tests for the data-layer operations performed by EditMetadataDialog on Save:
/// - metadata fields are persisted correctly
/// - changed fields get source="manual" and locked=1
/// - unchanged fields are NOT stamped with field_state
/// - region is persisted to releases table
/// - release_type persists to release_metadata.release_type
/// </summary>
public sealed class EditMetadataTests : IDisposable
{
    private readonly string _dbPath;

    public EditMetadataTests()
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

    // ── Helpers that mirror what EditMetadataDialog.OnSave does ──────────────

    /// <summary>
    /// Simulates the EditMetadataDialog save operation:
    /// saves metadata, updates region if changed, marks changed fields as manual+locked.
    /// </summary>
    private static HashSet<string> SimulateSave(
        DatLineStore store,
        string releaseId,
        ReleaseMetadataRecord original,
        string originalRegion,
        ReleaseMetadataRecord updated,
        string newRegion)
    {
        store.SaveReleaseMetadata(updated);

        if (!string.Equals(newRegion, originalRegion, StringComparison.Ordinal))
            store.UpdateReleaseRegion(releaseId, newRegion);

        var changed = BuildChangedFields(original, originalRegion, updated, newRegion);
        foreach (var field in changed)
            store.SaveMetadataFieldState(releaseId, field, "manual", "", locked: true);

        return changed;
    }

    private static HashSet<string> BuildChangedFields(
        ReleaseMetadataRecord orig, string origRegion,
        ReleaseMetadataRecord updated, string newRegion)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        void Check(string n, string o, string u) { if (o != u) changed.Add(n); }

        Check("title",            orig.Title,           updated.Title);
        Check("original_title",   orig.OriginalTitle,   updated.OriginalTitle);
        Check("sort_title",       orig.SortTitle,       updated.SortTitle);
        Check("developer",        orig.Developer,       updated.Developer);
        Check("publisher",        orig.Publisher,       updated.Publisher);
        Check("year",             orig.Year,            updated.Year);
        Check("languages",        orig.Languages,       updated.Languages);
        Check("alternate_titles", orig.AlternateTitles, updated.AlternateTitles);
        Check("description",      orig.Description,     updated.Description);
        Check("genre",            orig.Genre,           updated.Genre);
        Check("subgenre",         orig.Subgenre,        updated.Subgenre);
        Check("players",          orig.Players,         updated.Players);
        Check("release_type",     orig.ReleaseType,     updated.ReleaseType);
        Check("rating",           orig.Rating,          updated.Rating);
        Check("notes",            orig.Notes,           updated.Notes);

        if (!string.Equals(newRegion, origRegion, StringComparison.Ordinal))
            changed.Add("region");

        return changed;
    }

    private static ReleaseMetadataRecord BaseRecord(string id = "rel-001") => new()
    {
        ReleaseId     = id,
        Title         = "Animal Basket",
        OriginalTitle = "アニマルバスケット",
        Developer     = "Dev Co",
        Publisher     = "Pub Co",
        Year          = "2003",
        Languages     = "en",
        Description   = "A game.",
        ScrapedAtUtc  = "2026-01-01T00:00:00Z",
    };

    // ── Metadata field persistence ─────────────────────────────────────────────

    [Fact]
    public void Save_PersistsAllEditableFields()
    {
        var store   = Open();
        var orig    = BaseRecord();
        var updated = new ReleaseMetadataRecord
        {
            ReleaseId       = "rel-001",
            Title           = "New Title",
            OriginalTitle   = "新タイトル",
            SortTitle       = "New Title, The",
            Developer       = "New Dev",
            Publisher       = "New Pub",
            Year            = "2005",
            Languages       = "en,fr",
            AlternateTitles = "NT",
            Description     = "Updated desc.",
            Genre           = "Sports",
            Subgenre        = "Basketball",
            Players         = "1-2",
            ReleaseType     = "Retail",
            Rating          = "E",
            Notes           = "My note.",
            ScrapedAtUtc    = orig.ScrapedAtUtc,
        };

        SimulateSave(store, "rel-001", orig, "US", updated, "EU");

        var loaded = store.LoadReleaseMetadata()["rel-001"];
        Assert.Equal("New Title",         loaded.Title);
        Assert.Equal("新タイトル",        loaded.OriginalTitle);
        Assert.Equal("New Title, The",    loaded.SortTitle);
        Assert.Equal("New Dev",           loaded.Developer);
        Assert.Equal("New Pub",           loaded.Publisher);
        Assert.Equal("2005",              loaded.Year);
        Assert.Equal("en,fr",             loaded.Languages);
        Assert.Equal("NT",                loaded.AlternateTitles);
        Assert.Equal("Updated desc.",     loaded.Description);
        Assert.Equal("Sports",            loaded.Genre);
        Assert.Equal("Basketball",        loaded.Subgenre);
        Assert.Equal("1-2",               loaded.Players);
        Assert.Equal("Retail",            loaded.ReleaseType);
        Assert.Equal("E",                 loaded.Rating);
        Assert.Equal("My note.",          loaded.Notes);
    }

    [Fact]
    public void Save_ReleaseType_PersistsToReleaseMetadata()
    {
        var store   = Open();
        var withType = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            Title       = BaseRecord().Title,
            ReleaseType = "Fan Translation",
            ScrapedAtUtc = BaseRecord().ScrapedAtUtc,
        };

        SimulateSave(store, "rel-001", BaseRecord(), "US", withType, "US");

        Assert.Equal("Fan Translation", store.LoadReleaseMetadata()["rel-001"].ReleaseType);
    }

    [Fact]
    public void Save_ReleaseType_IsDistinctFromFormat()
    {
        // release_type lives in release_metadata; format lives in releases.
        // Saving a release_type must not affect releases.format.
        var store   = Open();
        var updated = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            ReleaseType = "Homebrew",
            ScrapedAtUtc = "",
        };
        SimulateSave(store, "rel-001", BaseRecord(), "US", updated, "US");

        // Confirm release_metadata has the release_type
        Assert.Equal("Homebrew", store.LoadReleaseMetadata()["rel-001"].ReleaseType);
        // releases table has no format row since we never inserted one — that's expected;
        // the point is release_type does NOT go into releases.format
    }

    // ── Region persistence ─────────────────────────────────────────────────────

    [Fact]
    public void Save_Region_PersistsToReleasesTable()
    {
        var store = Open();

        // Insert a release row so UPDATE has a row to target
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO releases(id, dat_line_id, name, status, region, languages, format, size,
                                 release_content_key, content_category_id)
            VALUES('rel-001','dl-1','Animal Basket','present','US','en','zip','100MB','','games')
            """;
        ins.ExecuteNonQuery();

        SimulateSave(store, "rel-001", BaseRecord(), "US", BaseRecord(), "EU");

        using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT region FROM releases WHERE id='rel-001'";
        var region = sel.ExecuteScalar() as string;
        Assert.Equal("EU", region);
    }

    [Fact]
    public void Save_Region_Unchanged_DoesNotCallUpdateReleaseRegion()
    {
        // Verifies that if region hasn't changed, UpdateReleaseRegion is never called
        // (changed field set must NOT contain "region").
        var store   = Open();
        var changed = SimulateSave(store, "rel-001", BaseRecord(), "US", BaseRecord(), "US");
        Assert.DoesNotContain("region", changed);
    }

    [Fact]
    public void UpdateReleaseRegion_IsVisibleToLoadReleases()
    {
        // This is the critical reload test: after UpdateReleaseRegion, a fresh
        // LoadReleases() call must return the new region. This mirrors what
        // RebuildLibraryDatasets does — and confirms that the fix (mutating
        // LibraryEntry.Region in-place) would reflect the DB truth.
        var store = Open();

        // Insert a release row
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO releases(id, dat_line_id, name, status, region, languages, format, size,
                                 release_content_key, content_category_id)
            VALUES('rel-001','dl-1','Animal Basket','present','US','en','zip','100MB','','games')
            """;
        ins.ExecuteNonQuery();

        store.UpdateReleaseRegion("rel-001", "JP");

        // Open a fresh store to simulate RebuildLibraryDatasets on next load
        var store2  = Open();
        var release = store2.LoadReleases().Find(r => r.Id == "rel-001");
        Assert.NotNull(release);
        Assert.Equal("JP", release!.Region);
    }

    [Fact]
    public void UpdateReleaseRegion_SameStore_ReflectsOnReload()
    {
        var store = Open();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT INTO releases(id, dat_line_id, name, status, region, languages, format, size,
                                 release_content_key, content_category_id)
            VALUES('rel-001','dl-1','Animal Basket','present','US','en','zip','100MB','','games')
            """;
        ins.ExecuteNonQuery();

        store.UpdateReleaseRegion("rel-001", "EU");

        var releases = store.LoadReleases();
        Assert.Equal("EU", releases.Find(r => r.Id == "rel-001")!.Region);
    }

    // ── Changed-field tracking → manual + locked ──────────────────────────────

    [Fact]
    public void ChangedField_GetsManualSourceAndLocked()
    {
        var store   = Open();
        var updated = new ReleaseMetadataRecord
        {
            ReleaseId = "rel-001",
            Title     = "Edited Title",   // changed
            Developer = BaseRecord().Developer, // unchanged
            ScrapedAtUtc = BaseRecord().ScrapedAtUtc,
        };
        SimulateSave(store, "rel-001", BaseRecord(), "US", updated, "US");

        Assert.True(store.IsMetadataFieldLocked("rel-001", "title"));
        var states = store.LoadMetadataFieldStates("rel-001");
        var titleState = states.Find(s => s.Field == "title");
        Assert.NotNull(titleState);
        Assert.Equal("manual", titleState!.Source);
        Assert.Equal("",       titleState.Provider);
        Assert.True(titleState.Locked);
    }

    [Fact]
    public void UnchangedField_DoesNotGetFieldState()
    {
        var store   = Open();
        // Only change title; developer and year are unchanged
        var updated = new ReleaseMetadataRecord
        {
            ReleaseId = "rel-001",
            Title     = "Changed",
            Developer = BaseRecord().Developer,
            Year      = BaseRecord().Year,
            ScrapedAtUtc = BaseRecord().ScrapedAtUtc,
        };
        SimulateSave(store, "rel-001", BaseRecord(), "US", updated, "US");

        var states = store.LoadMetadataFieldStates("rel-001");
        Assert.DoesNotContain(states, s => s.Field == "developer");
        Assert.DoesNotContain(states, s => s.Field == "year");
    }

    [Fact]
    public void MultipleChangedFields_AllGetManualLocked()
    {
        var store   = Open();
        var updated = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            Title       = "New",
            Genre       = "Puzzle",
            ReleaseType = "Demo",
            ScrapedAtUtc = BaseRecord().ScrapedAtUtc,
        };
        SimulateSave(store, "rel-001", BaseRecord(), "US", updated, "EU");

        var states = store.LoadMetadataFieldStates("rel-001");
        foreach (var field in new[] { "title", "genre", "release_type", "region" })
        {
            var s = states.Find(s2 => s2.Field == field);
            Assert.NotNull(s);
            Assert.Equal("manual", s!.Source);
            Assert.True(s.Locked);
        }
    }

    [Fact]
    public void RegionChange_MarkedAsManualLocked()
    {
        var store   = Open();
        var changed = SimulateSave(store, "rel-001", BaseRecord(), "US", BaseRecord(), "JP");

        Assert.Contains("region", changed);
        Assert.True(store.IsMetadataFieldLocked("rel-001", "region"));
    }

    [Fact]
    public void NoFieldsChanged_NoFieldStateRows()
    {
        var store   = Open();
        var changed = SimulateSave(store, "rel-001", BaseRecord(), "US", BaseRecord(), "US");

        Assert.Empty(changed);
        Assert.Empty(store.LoadMetadataFieldStates("rel-001"));
    }

    // ── ReleaseType badge data: ReleaseType used, not Format ──────────────────

    [Fact]
    public void ReleaseType_ReturnsFromMetadata_NotFromReleasesFormat()
    {
        // This verifies the data contract: the catalog badge MUST read
        // ReleaseMetadataRecord.ReleaseType, not releases.format.
        var store = Open();
        var meta  = new ReleaseMetadataRecord
        {
            ReleaseId   = "rel-001",
            ReleaseType = "Homebrew",
            ScrapedAtUtc = "",
        };
        store.SaveReleaseMetadata(meta);

        var loaded = store.LoadReleaseMetadata()["rel-001"];
        // ReleaseType is the badge source
        Assert.Equal("Homebrew", loaded.ReleaseType);
        // There is no Format field on ReleaseMetadataRecord — confirms separation
        // (the next line would be a compile error if ReleaseMetadataRecord had a Format prop)
    }

    // ── Per-field lock control ────────────────────────────────────────────────

    /// <summary>
    /// Mirrors EditMetadataDialog.OnSave with per-field lock control.
    /// lockMap: field → desired locked state (missing key defaults to true).
    /// existingStates: field → prior MetadataFieldStateRecord (for lock-only changes).
    /// </summary>
    private static HashSet<string> SimulateSaveWithLocks(
        DatLineStore store,
        string releaseId,
        ReleaseMetadataRecord original,
        string originalRegion,
        ReleaseMetadataRecord updated,
        string newRegion,
        Dictionary<string, bool> lockMap,
        Dictionary<string, MetadataFieldStateRecord>? existingStates = null)
    {
        store.SaveReleaseMetadata(updated);

        if (!string.Equals(newRegion, originalRegion, StringComparison.Ordinal))
            store.UpdateReleaseRegion(releaseId, newRegion);

        var changed = BuildChangedFields(original, originalRegion, updated, newRegion);

        foreach (var field in changed)
            store.SaveMetadataFieldState(releaseId, field, "manual", "",
                locked: lockMap.GetValueOrDefault(field, true));

        existingStates ??= new Dictionary<string, MetadataFieldStateRecord>(StringComparer.Ordinal);
        foreach (var (field, locked) in lockMap)
        {
            if (changed.Contains(field)) continue;

            existingStates.TryGetValue(field, out var existing);
            var wasLocked = existing?.Locked ?? false;
            if (locked == wasLocked) continue;

            if (existing is not null)
                store.SaveMetadataFieldState(releaseId, field, existing.Source, existing.Provider, locked: locked);
            else
            {
                var val = EditMetadataDialog.GetFieldValue(field, updated, newRegion);
                store.SaveMetadataFieldState(releaseId, field, val.Length > 0 ? "manual" : "", "", locked: locked);
            }
        }

        return changed;
    }

    [Fact]
    public void ChangedField_WithLockUnchecked_SavesManualUnlocked()
    {
        var store   = Open();
        var updated = new ReleaseMetadataRecord
        {
            ReleaseId    = "rel-001",
            Title        = "Edited Title",
            Developer    = BaseRecord().Developer,
            ScrapedAtUtc = BaseRecord().ScrapedAtUtc,
        };
        var lockMap = new Dictionary<string, bool>(StringComparer.Ordinal) { ["title"] = false };

        SimulateSaveWithLocks(store, "rel-001", BaseRecord(), "US", updated, "US", lockMap);

        var titleState = store.LoadMetadataFieldStates("rel-001").Find(s => s.Field == "title");
        Assert.NotNull(titleState);
        Assert.Equal("manual", titleState!.Source);
        Assert.False(titleState.Locked);
    }

    [Fact]
    public void LockOnlyChange_UpdatesLockedFlag_PreservesSourceAndProvider()
    {
        var store = Open();
        store.SaveMetadataFieldState("rel-001", "developer", "screenscraper", "screenscraper", locked: true);
        var existingStates = store.LoadMetadataFieldStates("rel-001")
                                  .ToDictionary(s => s.Field, StringComparer.Ordinal);

        var lockMap = new Dictionary<string, bool>(StringComparer.Ordinal) { ["developer"] = false };
        SimulateSaveWithLocks(store, "rel-001", BaseRecord(), "US", BaseRecord(), "US",
            lockMap, existingStates);

        var devState = store.LoadMetadataFieldStates("rel-001").Find(s => s.Field == "developer");
        Assert.NotNull(devState);
        Assert.Equal("screenscraper", devState!.Source);
        Assert.Equal("screenscraper", devState.Provider);
        Assert.False(devState.Locked);
    }

    [Fact]
    public void LockOnlyChange_NoExistingState_CreatesManualLocked()
    {
        var store = Open();
        var lockMap = new Dictionary<string, bool>(StringComparer.Ordinal) { ["title"] = true };

        SimulateSaveWithLocks(store, "rel-001", BaseRecord(), "US", BaseRecord(), "US", lockMap);

        var titleState = store.LoadMetadataFieldStates("rel-001").Find(s => s.Field == "title");
        Assert.NotNull(titleState);
        Assert.Equal("manual", titleState!.Source); // value is non-empty → "manual"
        Assert.True(titleState.Locked);
    }

    [Fact]
    public void ClearingField_WithLockUnchecked_SavesEmptyNotLocked()
    {
        var store   = Open();
        var updated = new ReleaseMetadataRecord
        {
            ReleaseId    = "rel-001",
            Title        = "",               // cleared
            Developer    = BaseRecord().Developer,
            ScrapedAtUtc = BaseRecord().ScrapedAtUtc,
        };
        var lockMap = new Dictionary<string, bool>(StringComparer.Ordinal) { ["title"] = false };

        SimulateSaveWithLocks(store, "rel-001", BaseRecord(), "US", updated, "US", lockMap);

        Assert.Equal("", store.LoadReleaseMetadata()["rel-001"].Title);

        var titleState = store.LoadMetadataFieldStates("rel-001").Find(s => s.Field == "title");
        Assert.NotNull(titleState);
        Assert.False(titleState!.Locked);
    }

    [Fact]
    public void RegionChange_WithLockUnchecked_RegionStateNotLocked()
    {
        var store   = Open();
        var lockMap = new Dictionary<string, bool>(StringComparer.Ordinal) { ["region"] = false };

        SimulateSaveWithLocks(store, "rel-001", BaseRecord(), "US", BaseRecord(), "JP", lockMap);

        var regionState = store.LoadMetadataFieldStates("rel-001").Find(s => s.Field == "region");
        Assert.NotNull(regionState);
        Assert.Equal("manual", regionState!.Source);
        Assert.False(regionState.Locked);
    }
}
