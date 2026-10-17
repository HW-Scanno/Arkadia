using System;
using System.Linq;
using Arkadia.Data;
using Arkadia.Data.Identifiers;
using Arkadia.Ingestion;
using Xunit;

namespace Arkadia.Tests.Ingestion;

/// <summary>DAT-NAMING-2: the Import DAT dialog's identity logic (UI-free form over SingleDatIdBuilder).</summary>
public sealed class SingleDatImportFormTests
{
    private static SingleDatImportForm Form(params string[] existing)
    {
        var f = new SingleDatImportForm(existing);
        f.SelectSystem("nds");
        f.SelectAuthority("nointro", "NoIntro");
        f.SelectMediaType("cartridge");
        return f;
    }

    // ── Default + id composition ────────────────────────────────────────────────

    [Fact]
    public void Default_VariantIsMain_AndIdUsesTheCurrentSelections()
    {
        var f = Form();
        Assert.Equal("main", f.Variant);
        Assert.Equal("nds-nointro-cartridge-main", f.FinalId);
        Assert.Equal("nds-nointro-cartridge-main", f.PreviewId);
        Assert.True(f.IsValid);
        Assert.Null(f.FirstError);
    }

    [Fact]
    public void ChangingSystemAuthorityOrMedia_UpdatesTheProposedId()
    {
        var f = Form();
        f.SelectMediaType("other");
        Assert.Equal("nds-nointro-other-main", f.FinalId);
        f.SelectAuthority("local", "Local");
        Assert.Equal("nds-local-other-main", f.FinalId);
        f.SelectSystem("psx");
        Assert.Equal("psx-local-other-main", f.FinalId);
        f.SetVariant("sbi");
        Assert.Equal("psx-local-other-sbi", f.FinalId);
    }

    [Fact]
    public void ChangingTheVariant_UpdatesTheProposedId()
    {
        var f = Form();
        f.SetVariant("downloadplay");
        Assert.Equal("nds-nointro-cartridge-downloadplay", f.FinalId);
    }

    [Fact]
    public void IncompleteSelection_HasNoIdAndAnExplanatoryError()
    {
        var f = new SingleDatImportForm(Array.Empty<string>());
        Assert.False(f.SelectionComplete);
        Assert.Null(f.FinalId);
        Assert.Equal("", f.PreviewId);
        Assert.False(f.IsValid);
        Assert.Contains("Select a system", f.FirstError);

        f.SelectSystem("nds"); f.SelectAuthority("nointro", "NoIntro");
        Assert.False(f.IsValid);                       // media still missing
        f.SelectSystem("");                            // the dialog's blank first system item
        f.SelectMediaType("cartridge");
        Assert.False(f.IsValid);
    }

    // ── Validation / blocking ───────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("Main")]
    [InlineData("download play")]
    [InlineData("a_b")]
    [InlineData("../x")]
    [InlineData(" main")]
    [InlineData("main\n")]
    public void InvalidVariant_BlocksImport_AndIsNeverRewritten(string variant)
    {
        var f = Form();
        f.SetVariant(variant);
        Assert.False(f.IsValid);
        Assert.Null(f.FinalId);
        Assert.NotNull(f.FirstError);
        Assert.Equal(variant, f.Variant);              // exactly what was typed
    }

    [Fact]
    public void InvalidVariant_StillPreviewsTheAttemptedComposition()
    {
        var f = Form();
        f.SetVariant("Bad Value");
        Assert.Equal("nds-nointro-cartridge-Bad Value", f.PreviewId);
    }

    [Theory]
    [InlineData("nds-nointro-cartridge-main")]
    [InlineData("NDS-NOINTRO-CARTRIDGE-MAIN")]
    public void CaseInsensitiveCollision_BlocksImport(string existing)
    {
        var f = Form(existing);
        Assert.False(f.IsValid);
        Assert.Equal(SingleDatIdError.Collision, f.IdResult!.Error);
        Assert.Contains("already exists", f.FirstError);
    }

    [Fact]
    public void CollisionWithAGroupLeafId_BlocksImport()
    {
        var f = Form("nds-nointro-cartridge-main");     // a leaf id is just another existing dat_lines.id
        Assert.False(f.IsValid);
        f.SetVariant("other");
        Assert.True(f.IsValid);
    }

    [Fact]
    public void LegacyThreeComponentId_DoesNotBlockTheNewDefault()
    {
        var f = Form("nds-nointro-cartridge");
        Assert.True(f.IsValid);
        Assert.Equal("nds-nointro-cartridge-main", f.FinalId);
    }

    // ── Display name ────────────────────────────────────────────────────────────

    [Fact]
    public void DisplayName_IsSuggestedFromAuthorityAndVariant()
    {
        var f = Form();
        Assert.Equal("NoIntro - Main", f.DisplayName);
        f.SetVariant("download-play");
        Assert.Equal("NoIntro - Download Play", f.DisplayName);
        f.SelectAuthority("local", "Local");
        Assert.Equal("Local - Download Play", f.DisplayName);
    }

    [Fact]
    public void DisplayName_UserEditIsPreservedAcrossSelectionAndVariantChanges()
    {
        var f = Form();
        f.SetDisplayNameFromUser("My Custom Name");
        Assert.True(f.DisplayNameEdited);

        f.SelectAuthority("local", "Local");
        f.SelectMediaType("other");
        f.SelectSystem("psx");
        f.SetVariant("sbi");

        Assert.Equal("My Custom Name", f.DisplayName);
        Assert.Equal("psx-local-other-sbi", f.FinalId);   // id still follows the selections
    }

    [Fact]
    public void DisplayName_IsIndependentOfTheId()
    {
        var f = Form();
        f.SetDisplayNameFromUser("Totally Different Label");
        Assert.Equal("nds-nointro-cartridge-main", f.FinalId);
        Assert.DoesNotContain("different", f.FinalId!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankDisplayName_BlocksImport(string name)
    {
        var f = Form();
        f.SetDisplayNameFromUser(name);
        Assert.False(f.IsValid);
        Assert.Contains("Display Name", f.FirstError);
        Assert.NotNull(f.FinalId);                       // the id itself is fine; the name is what blocks
    }

    [Fact]
    public void TooLongOrControlCharacterDisplayName_BlocksImport()
    {
        var f = Form();
        f.SetDisplayNameFromUser(new string('x', DatLineDisplayName.MaxLength + 1));
        Assert.False(f.IsValid);
        f.SetDisplayNameFromUser("bad\tname");
        Assert.False(f.IsValid);
        f.SetDisplayNameFromUser(new string('x', DatLineDisplayName.MaxLength));
        Assert.True(f.IsValid);
    }

    [Theory]
    [InlineData("NoIntro", "main", "NoIntro - Main")]
    [InlineData("Local",   "sbi",  "Local - Sbi")]
    [InlineData("",        "main", "Main")]
    [InlineData(null,      null,   "Main")]
    [InlineData("A",       "Ünï",  "A - Uni")]
    public void Suggest_Examples(string? authority, string? variant, string expected)
        => Assert.Equal(expected, DatLineDisplayName.Suggest(authority, variant));
}
