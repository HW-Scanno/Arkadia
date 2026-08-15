using System;
using System.IO;
using System.Linq;
using Arkadia.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arkadia.Tests.Data;

/// <summary>
/// Covers the zip_compression folder-transform wildcard fix. The historical default
/// (<c>a -tzip "{output}" * -w"{input}"</c>) resolved a bare '*' from Arkadia's inherited
/// process working directory (recursively compressing the whole app/data tree) and misused
/// '-w' as if it scoped input. The fix roots the wildcard explicitly under the release source
/// folder (<c>a -tzip "{output}" "{input}\*"</c>) and drops '-w'. New catalogs seed the fixed
/// value; existing catalogs migrate in place, guarded by the EXACT historical default so a
/// user-customized template is never overwritten. Temp catalogs only.
/// </summary>
public sealed class CatalogServiceZipCompressionTransformTests : IDisposable
{
    private const string Fixed  = @"a -tzip ""{output}"" ""{input}\*""";
    private const string Buggy  = @"a -tzip ""{output}"" * -w""{input}""";

    private readonly string _dir;

    public CatalogServiceZipCompressionTransformTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string DbPath => Path.Combine(_dir, "catalog.db");

    private string LoadZipTemplate()
        => new CatalogService(_dir).LoadTransforms().Single(t => t.Id == "zip_compression").CommandTemplate;

    private void SetZipTemplateRaw(string value)
    {
        _ = new CatalogService(_dir);   // ensure the DB + row exist
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE transforms SET command_template = $v WHERE transform_id = 'zip_compression'";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private string ReadZipTemplateRaw()
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT command_template FROM transforms WHERE transform_id = 'zip_compression'";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact] // A. new catalog seeds the fixed template; no '*'/'-w'
    public void Seed_ZipCompression_UsesFixedTemplate()
    {
        Assert.Equal(Fixed, LoadZipTemplate());
        Assert.DoesNotContain(" * ", LoadZipTemplate());
        Assert.DoesNotContain("-w", LoadZipTemplate());
    }

    [Fact] // A(guard): sibling/other transforms unchanged
    public void Seed_OtherTransforms_Unchanged()
    {
        var all = new CatalogService(_dir).LoadTransforms();
        Assert.Equal(@"a -tzip ""{output}"" ""{input}""", all.Single(t => t.Id == "zip_file_compression").CommandTemplate);
        Assert.Equal("createcd -i \"{input}\" -o \"{output}\"", all.Single(t => t.Id == "chd_cd_compression").CommandTemplate);
    }

    [Fact] // B. exact historical buggy default migrates to the fixed template
    public void Migration_HistoricalBuggyDefault_IsFixed()
    {
        SetZipTemplateRaw(Buggy);
        Assert.Equal(Buggy, ReadZipTemplateRaw());   // precondition

        _ = new CatalogService(_dir);                 // reopen → EnsureSchema runs the migration
        Assert.Equal(Fixed, ReadZipTemplateRaw());
    }

    [Fact] // C. a user-customized template is preserved (not the exact historical default → guard misses)
    public void Migration_CustomTemplate_Preserved()
    {
        const string custom = @"a -tzip -mx=9 ""{output}"" ""{input}\*""";
        SetZipTemplateRaw(custom);

        _ = new CatalogService(_dir);
        Assert.Equal(custom, ReadZipTemplateRaw());
    }

    [Fact] // C(variant): a partially-buggy value that is NOT byte-exact is left untouched
    public void Migration_NonExactBuggyValue_Preserved()
    {
        const string nearBuggy = @"a -tzip ""{output}"" * -w ""{input}"""; // extra space before "{input}"
        SetZipTemplateRaw(nearBuggy);

        _ = new CatalogService(_dir);
        Assert.Equal(nearBuggy, ReadZipTemplateRaw());
    }

    [Fact] // D. idempotent: re-running initialization on an already-fixed catalog changes nothing
    public void Migration_Idempotent()
    {
        Assert.Equal(Fixed, LoadZipTemplate());   // first construction seeds fixed
        _ = new CatalogService(_dir);             // second construction: guard no longer matches
        Assert.Equal(Fixed, ReadZipTemplateRaw());
    }

    [Fact] // E. BuildCommand on the fixed template roots the wildcard under {input}; no bare '*', no '-w'
    public void BuildCommand_FixedTemplate_RootsWildcardUnderInput()
    {
        var input  = @"G:\Arkadia\source\c64\c64-tosec-firmware-crt\Formel64 v1.0 (1985)(Grewe Computertechnik)";
        var output = @"G:\Arkadia\archive\c64\c64-tosec-firmware-crt\Formel64 v1.0 (1985)(Grewe Computertechnik).zip";

        var args = TransformEngine.BuildCommand(Fixed, input, output);

        Assert.Equal($@"a -tzip ""{output}"" ""{input}\*""", args);
        Assert.Contains($@"""{input}\*""", args);   // wildcard explicitly rooted under the release source folder
        Assert.DoesNotContain(" * ", args);         // no bare standalone wildcard resolved from the process CWD
        Assert.DoesNotContain("-w", args);          // no temp-working-directory switch
    }
}
