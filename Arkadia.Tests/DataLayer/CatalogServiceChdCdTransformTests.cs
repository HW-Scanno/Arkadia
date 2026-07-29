using System;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// Covers the "CHD CD Compression" → "CHD CD/GD Compression" display-label rename:
/// internal transform identity/command must be untouched, and existing catalogs
/// (seeded before the rename) must migrate their display name in place.
/// </summary>
public sealed class CatalogServiceChdCdTransformTests : IDisposable
{
    private readonly string _dir;

    public CatalogServiceChdCdTransformTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, "catalog.db");

    [Fact]
    public void DatConfig_ChdCdProcessor_DisplayName_IsCdGdCompression()
    {
        var catalog = new CatalogService(_dir);
        var xform = catalog.LoadTransforms().Single(t => t.Id == "chd_cd_compression");
        Assert.Equal("CHD CD/GD Compression", xform.Name);
    }

    [Fact]
    public void DatConfig_ChdCdProcessor_InternalIdentity_IsPreserved()
    {
        var catalog = new CatalogService(_dir);
        var xform = catalog.LoadTransforms().Single(t => t.Id == "chd_cd_compression");

        Assert.Equal("chd_cd_compression", xform.Id);
        Assert.Equal("chdman", xform.ToolId);
        Assert.Equal("createcd -i \"{input}\" -o \"{output}\"", xform.CommandTemplate);
        Assert.Equal(".chd", xform.OutputExtension);
        Assert.True(xform.IsEnabled);
    }

    [Fact]
    public void DatConfig_DvdIso_DoesNotUseCdGdCompression()
    {
        var catalog = new CatalogService(_dir);
        var dvd = catalog.LoadTransforms().Single(t => t.Id == "chd_dvd_compression");

        Assert.Equal("CHD DVD Compression", dvd.Name);
        Assert.Equal("createdvd -i \"{input}\" -o \"{output}\"", dvd.CommandTemplate);
        Assert.DoesNotContain("CD/GD", dvd.Name);
    }

    [Fact]
    public void DatConfig_LegacyGdAndDreamcastTransforms_AreDisabledAndLabeledLegacy()
    {
        var catalog = new CatalogService(_dir);
        var all = catalog.LoadTransforms();

        var gd = all.Single(t => t.Id == "chd_gd_compression");
        Assert.False(gd.IsEnabled);
        Assert.Contains("legacy", gd.Name, StringComparison.OrdinalIgnoreCase);

        var dreamcast = all.Single(t => t.Id == "chd_dreamcast_compression");
        Assert.False(dreamcast.IsEnabled);
        Assert.Contains("legacy", dreamcast.Name, StringComparison.OrdinalIgnoreCase);

        // Still the same createcd-family command as before the rename — only
        // display/visibility changed, not behavior.
        Assert.Equal("createcd -i \"{input}\" -o \"{output}\"", gd.CommandTemplate);
        Assert.Equal("createcd -c zstd -i \"{input}\" -o \"{output}\"", dreamcast.CommandTemplate);
    }

    [Fact]
    public void DatConfig_ChdCdProcessor_LegacyDisplayName_LoadsIfPersisted()
    {
        // Simulate a catalog.db created before the rename shipped: seed the table
        // by hand with the old label, exactly as the old INSERT would have.
        SeedPreRenameCatalog();

        // Reopening via CatalogService (EnsureSchema runs the migration) must
        // update the display name in place without touching the identity.
        var catalog = new CatalogService(_dir);
        var xform = catalog.LoadTransforms().Single(t => t.Id == "chd_cd_compression");

        Assert.Equal("CHD CD/GD Compression", xform.Name);
        Assert.Equal("chd_cd_compression", xform.Id);
        Assert.Equal("createcd -i \"{input}\" -o \"{output}\"", xform.CommandTemplate);
    }

    [Fact]
    public void DatConfig_SavedExtensionMapping_ToLegacyGdTransform_StillResolves()
    {
        // A DAT line configured before the rename may have manually mapped an
        // extension to chd_gd_compression via "Per file extension". That saved
        // config must keep loading and resolving to the same transform.
        SeedPreRenameCatalog();
        using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dat_line_extension_transforms(dat_line_id, file_extension, transform_id, is_discard)
                VALUES ('dl-1', '.gdi', 'chd_gd_compression', 0)
                """;
            cmd.ExecuteNonQuery();
        }

        var catalog = new CatalogService(_dir);
        var mappings = catalog.LoadExtensionMappings("dl-1");
        var mapping = Assert.Single(mappings);
        Assert.Equal("chd_gd_compression", mapping.TransformId);

        // The transform itself is still loadable (just disabled/relabeled), so
        // the saved mapping still resolves to a real, working transform.
        var xform = catalog.LoadTransforms().Single(t => t.Id == mapping.TransformId);
        Assert.Equal("createcd -i \"{input}\" -o \"{output}\"", xform.CommandTemplate);
    }

    [Fact]
    public void DatConfig_UserRenamedTransform_IsNotOverwrittenByMigration()
    {
        // If a user already customized the display name via Manage Transforms
        // before ever seeing the migration, the migration must not clobber it —
        // it only fires when the name still matches the old seeded default.
        SeedPreRenameCatalog();
        using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE transforms SET name = 'My Custom CD Label' WHERE transform_id = 'chd_cd_compression'";
            cmd.ExecuteNonQuery();
        }

        var catalog = new CatalogService(_dir);
        var xform = catalog.LoadTransforms().Single(t => t.Id == "chd_cd_compression");
        Assert.Equal("My Custom CD Label", xform.Name);
    }

    /// <summary>
    /// Recreates the on-disk state of a catalog.db seeded before the CHD CD/GD
    /// rename shipped, using the exact old seed values.
    /// </summary>
    private void SeedPreRenameCatalog()
    {
        // First construction runs the current (post-rename) seed + migration —
        // start clean, then overwrite the seeded rows with the pre-rename values
        // to simulate an older install.
        _ = new CatalogService(_dir);

        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE transforms SET name = 'CHD CD Compression'   WHERE transform_id = 'chd_cd_compression';
            UPDATE transforms SET name = 'CHD GD Compression', is_enabled = 1
                WHERE transform_id = 'chd_gd_compression';
            UPDATE transforms SET name = 'CHD Compression (Dreamcast)', is_enabled = 1
                WHERE transform_id = 'chd_dreamcast_compression';
            """;
        cmd.ExecuteNonQuery();
    }
}
