using System;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// RVZ-SUPPORT-01 Phase A: the seeded rvz_compression command gains DolphinTool's mandatory block size, and existing
/// catalogs are corrected ONLY when the template still equals the exact historical defective default.
/// </summary>
public sealed class CatalogServiceRvzTransformTests : IDisposable
{
    private const string Fixed      = "convert -f rvz -b 131072 -c zstd -l 5 -i \"{input}\" -o \"{output}\"";
    private const string Historical = "convert -f rvz -c zstd -l 5 -i \"{input}\" -o \"{output}\"";

    private readonly string _dir;

    public CatalogServiceRvzTransformTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ArkRvzCat_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DbPath => Path.Combine(_dir, "catalog.db");

    private TransformRecord Rvz() => new CatalogService(_dir).LoadTransforms().Single(t => t.Id == "rvz_compression");

    private void SetRow(string template, string tier)
    {
        _ = new CatalogService(_dir);   // ensure the schema + seed exist
        using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE transforms SET command_template = $t, archive_tier = $tier WHERE transform_id = 'rvz_compression'";
            cmd.Parameters.AddWithValue("$t", template);
            cmd.Parameters.AddWithValue("$tier", tier);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void FreshCatalog_SeedsLosslessRvzCommand_WithBlockSize_TierA()
    {
        var x = Rvz();
        Assert.Equal(Fixed, x.CommandTemplate);
        Assert.DoesNotContain("-s", x.CommandTemplate.Split(' '));   // never scrub
        Assert.Equal("dolphintool", x.ToolId);
        Assert.Equal(".rvz", x.OutputExtension);
        Assert.Equal("A", x.ArchiveTier);
        Assert.True(x.IsEnabled && x.IsFileOriented && x.OutputIsFile);
    }

    [Fact]
    public void ExistingCatalog_WithHistoricalDefault_IsCorrected()
    {
        SetRow(Historical, "B");
        var x = Rvz();
        Assert.Equal(Fixed, x.CommandTemplate);
        Assert.Equal("A", x.ArchiveTier);
    }

    [Fact]
    public void HistoricalTemplate_WithUserChosenTier_FixesTemplate_KeepsTier()
    {
        SetRow(Historical, "C");
        var x = Rvz();
        Assert.Equal(Fixed, x.CommandTemplate);
        Assert.Equal("C", x.ArchiveTier);
    }

    [Theory]
    [InlineData("convert -f rvz -b 65536 -c lzma -l 9 -i \"{input}\" -o \"{output}\"")]
    [InlineData("convert -f rvz -c zstd -l 19 -i \"{input}\" -o \"{output}\"")]
    public void UserCustomizedTemplate_IsNeverOverwritten(string custom)
    {
        SetRow(custom, "B");
        var x = Rvz();
        Assert.Equal(custom, x.CommandTemplate);
        Assert.Equal("B", x.ArchiveTier);
    }

    [Fact]
    public void Migration_IsIdempotent()
    {
        SetRow(Historical, "B");
        _ = Rvz();
        SqliteConnection.ClearAllPools();
        var x = Rvz();
        Assert.Equal(Fixed, x.CommandTemplate);
        Assert.Equal("A", x.ArchiveTier);
    }

    [Fact]
    public void OtherTransforms_AndToolRegistry_AreUnchanged()
    {
        SetRow(Historical, "B");
        var cat = new CatalogService(_dir);
        var all = cat.LoadTransforms().ToDictionary(t => t.Id);

        Assert.Equal("B", all["chd_cd_compression"].ArchiveTier);
        Assert.Equal("B", all["chd_dvd_compression"].ArchiveTier);
        Assert.Equal("B", all["wux_compression"].ArchiveTier);
        Assert.Equal("A", all["zip_file_compression"].ArchiveTier);
        Assert.Equal("createdvd -i \"{input}\" -o \"{output}\"", all["chd_dvd_compression"].CommandTemplate);

        var tool = cat.LoadTools().Single(t => t.Id == "dolphintool");
        Assert.Equal("dolphintool", tool.FolderName);
        Assert.Equal("DolphinTool.exe", tool.ExecutableName);
    }
}
