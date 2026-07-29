using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arkadia;
using Arkadia.Data;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// Tests for MetadataValueNormalizer (pure unit) and CatalogService mapping
/// CRUD + seeding (integration, uses a temp catalog.db).
/// </summary>
public sealed class MetadataValueMappingTests : IDisposable
{
    private readonly string _dataDir;

    public MetadataValueMappingTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private CatalogService Open() => new(_dataDir);

    // ── MetadataValueNormalizer — pure unit tests (no DB) ────────────────────

    private static List<MetadataValueMappingRecord> RegionMappings() =>
    [
        new("region", "wor",    "World",  Enabled: true),
        new("region", "eu",     "Europe", Enabled: true),
        new("region", "us",     "USA",    Enabled: true),
        new("region", "hidden", "Secret", Enabled: false),
    ];

    private static List<MetadataValueMappingRecord> ReleaseTypeMappings() =>
    [
        new("release_type", "retail",          "Retail",          Enabled: true),
        new("release_type", "fantranslation",   "Fan Translation", Enabled: true),
        new("release_type", "fan-translation",  "Fan Translation", Enabled: true),
    ];

    [Fact]
    public void Normalizer_RegionWor_NormalizesToWorld()
    {
        var result = MetadataValueNormalizer.Normalize("region", "wor", RegionMappings());
        Assert.Equal("World", result);
    }

    [Fact]
    public void Normalizer_RegionWOR_CaseInsensitive_NormalizesToWorld()
    {
        var result = MetadataValueNormalizer.Normalize("region", "WOR", RegionMappings());
        Assert.Equal("World", result);
    }

    [Fact]
    public void Normalizer_RegionWithWhitespace_TrimsAndNormalizes()
    {
        var result = MetadataValueNormalizer.Normalize("region", "  wor  ", RegionMappings());
        Assert.Equal("World", result);
    }

    [Fact]
    public void Normalizer_UnknownValue_ReturnsTrimmedUnchanged()
    {
        var result = MetadataValueNormalizer.Normalize("region", "australia", RegionMappings());
        Assert.Equal("australia", result);
    }

    [Fact]
    public void Normalizer_DisabledMapping_IsIgnored()
    {
        var result = MetadataValueNormalizer.Normalize("region", "hidden", RegionMappings());
        Assert.Equal("hidden", result); // not "Secret"
    }

    [Fact]
    public void Normalizer_EmptyValue_ReturnedUnchanged()
    {
        var result = MetadataValueNormalizer.Normalize("region", "", RegionMappings());
        Assert.Equal("", result);
    }

    [Fact]
    public void Normalizer_EmptyMappingsList_ReturnsTrimmedValue()
    {
        var result = MetadataValueNormalizer.Normalize("region", " wor ", []);
        Assert.Equal("wor", result);
    }

    [Fact]
    public void Normalizer_ReleaseTypeFanTranslation_Normalizes()
    {
        var result = MetadataValueNormalizer.Normalize("release_type", "fantranslation", ReleaseTypeMappings());
        Assert.Equal("Fan Translation", result);
    }

    [Fact]
    public void Normalizer_ReleaseTypeFanHyphenTranslation_Normalizes()
    {
        var result = MetadataValueNormalizer.Normalize("release_type", "fan-translation", ReleaseTypeMappings());
        Assert.Equal("Fan Translation", result);
    }

    [Fact]
    public void Normalizer_ReleaseTypeRetail_Normalizes()
    {
        var result = MetadataValueNormalizer.Normalize("release_type", "retail", ReleaseTypeMappings());
        Assert.Equal("Retail", result);
    }

    [Fact]
    public void Normalizer_DifferentField_DoesNotMatch()
    {
        // "wor" is a region mapping; should not match when field is "genre"
        var result = MetadataValueNormalizer.Normalize("genre", "wor", RegionMappings());
        Assert.Equal("wor", result);
    }

    // ── Manual save normalization (tests the normalizer path used by OnSave) ──

    [Fact]
    public void ManualSave_RegionWor_NormalizesToWorld()
    {
        // Simulates EditMetadataDialog.OnSave normalizing region before saving
        var mappings = Open().LoadMetadataValueMappings();
        var normalized = MetadataValueNormalizer.Normalize("region", "wor", mappings);
        Assert.Equal("World", normalized);
    }

    [Fact]
    public void ManualSave_RegionUppercase_NormalizesToWorld()
    {
        var mappings = Open().LoadMetadataValueMappings();
        var normalized = MetadataValueNormalizer.Normalize("region", "WOR", mappings);
        Assert.Equal("World", normalized);
    }

    [Fact]
    public void ManualSave_ReleaseTypeRetail_NormalizesToRetail()
    {
        var mappings = Open().LoadMetadataValueMappings();
        var normalized = MetadataValueNormalizer.Normalize("release_type", "retail", mappings);
        Assert.Equal("Retail", normalized);
    }

    // ── CatalogService — default mappings seeding ─────────────────────────────

    [Fact]
    public void DefaultMappings_AreSeeded_OnFirstOpen()
    {
        var catalog  = Open();
        var mappings = catalog.LoadMetadataValueMappings();
        Assert.NotEmpty(mappings);
    }

    [Fact]
    public void DefaultMappings_SeededIdempotently_NoDuplicates()
    {
        // Open twice — second open must not duplicate rows
        _ = Open();
        var catalog  = Open();
        var mappings = catalog.LoadMetadataValueMappings();
        var regionWor = mappings.Where(m => m.Field == "region" && m.MatchValue == "wor").ToList();
        Assert.Single(regionWor);
    }

    [Fact]
    public void DefaultMapping_RegionWor_NormalizesToWorld()
    {
        var catalog = Open();
        var result  = catalog.NormalizeMetadataValue("region", "wor");
        Assert.Equal("World", result);
    }

    [Fact]
    public void DefaultMapping_RegionEu_NormalizesToEurope()
    {
        Assert.Equal("Europe", Open().NormalizeMetadataValue("region", "eu"));
    }

    [Fact]
    public void DefaultMapping_RegionUs_NormalizesToUSA()
    {
        Assert.Equal("USA", Open().NormalizeMetadataValue("region", "us"));
    }

    [Fact]
    public void DefaultMapping_RegionJp_NormalizesToJapan()
    {
        Assert.Equal("Japan", Open().NormalizeMetadataValue("region", "jp"));
    }

    [Fact]
    public void DefaultMapping_ReleaseTypeFanTranslation_Normalizes()
    {
        Assert.Equal("Fan Translation", Open().NormalizeMetadataValue("release_type", "fantranslation"));
    }

    // ── CatalogService — CRUD ─────────────────────────────────────────────────

    [Fact]
    public void SaveMapping_CanBeLoadedBack()
    {
        var catalog = Open();
        catalog.SaveMetadataValueMapping("genre", "rpg", "RPG", enabled: true);

        var mappings = catalog.LoadMetadataValueMappings();
        var m = mappings.Find(x => x.Field == "genre" && x.MatchValue == "rpg");
        Assert.NotNull(m);
        Assert.Equal("RPG",  m!.Replacement);
        Assert.True(m.Enabled);
    }

    [Fact]
    public void SaveMapping_Upserts_Replacement()
    {
        var catalog = Open();
        catalog.SaveMetadataValueMapping("genre", "rpg", "RPG",            enabled: true);
        catalog.SaveMetadataValueMapping("genre", "rpg", "Role-Playing",   enabled: true);

        var m = catalog.LoadMetadataValueMappings()
                       .Find(x => x.Field == "genre" && x.MatchValue == "rpg");
        Assert.Equal("Role-Playing", m!.Replacement);
    }

    [Fact]
    public void SaveMapping_Disabled_IsIgnoredByNormalizer()
    {
        var catalog = Open();
        catalog.SaveMetadataValueMapping("genre", "action", "Action", enabled: false);

        var result = catalog.NormalizeMetadataValue("genre", "action");
        Assert.Equal("action", result); // no match → returns trimmed input
    }

    [Fact]
    public void DeleteMapping_RemovesIt()
    {
        var catalog = Open();
        catalog.SaveMetadataValueMapping("genre", "rpg", "RPG", enabled: true);
        catalog.DeleteMetadataValueMapping("genre", "rpg");

        var m = catalog.LoadMetadataValueMappings()
                       .Find(x => x.Field == "genre" && x.MatchValue == "rpg");
        Assert.Null(m);
    }

    // ── MappingRowVm — view-model unit tests ─────────────────────────────────

    [Fact]
    public void MappingRowVm_ReflectsRecord()
    {
        var record = new MetadataValueMappingRecord("region", "wor", "World", Enabled: true);
        var vm     = new MappingRowVm(record);
        Assert.Equal("region", vm.Field);
        Assert.Equal("wor",    vm.MatchValue);
        Assert.Equal("World",  vm.Replacement);
        Assert.True(vm.Enabled);
    }

    [Fact]
    public void MappingRowVm_DisabledRecord_LoadsDisabled()
    {
        var record = new MetadataValueMappingRecord("region", "hidden", "Secret", Enabled: false);
        var vm     = new MappingRowVm(record);
        Assert.False(vm.Enabled);
    }

    [Fact]
    public void MappingRowVm_EnabledToggle_FiresPropertyChanged()
    {
        var record = new MetadataValueMappingRecord("region", "wor", "World", Enabled: true);
        var vm     = new MappingRowVm(record);
        var fired  = new List<string?>();
        vm.PropertyChanged += (_, e) => fired.Add(e.PropertyName);
        vm.Enabled = false;
        Assert.Contains(nameof(MappingRowVm.Enabled), fired);
        Assert.False(vm.Enabled);
    }

    [Fact]
    public void MappingRowVm_NoPropertyChangedWhenValueUnchanged()
    {
        var record = new MetadataValueMappingRecord("region", "wor", "World", Enabled: true);
        var vm     = new MappingRowVm(record);
        var fired  = false;
        vm.PropertyChanged += (_, _) => fired = true;
        vm.Enabled = true; // same value — should not fire
        Assert.False(fired);
    }

    [Fact]
    public void SaveMapping_ReloadReflectsChange()
    {
        var catalog = Open();
        catalog.SaveMetadataValueMapping("genre", "rpg", "RPG", enabled: true);
        catalog.SaveMetadataValueMapping("genre", "rpg", "Role-Playing", enabled: false);

        var m = catalog.LoadMetadataValueMappings()
                       .Find(x => x.Field == "genre" && x.MatchValue == "rpg");
        Assert.NotNull(m);
        Assert.Equal("Role-Playing", m!.Replacement);
        Assert.False(m.Enabled);
    }

    [Fact]
    public void DeleteMapping_DefaultRow_CanBeRemoved()
    {
        var catalog = Open();
        catalog.DeleteMetadataValueMapping("region", "wor");

        var m = catalog.LoadMetadataValueMappings()
                       .Find(x => x.Field == "region" && x.MatchValue == "wor");
        Assert.Null(m);
    }
}
