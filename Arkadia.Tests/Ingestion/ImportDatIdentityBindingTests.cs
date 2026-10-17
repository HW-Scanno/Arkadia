using System;
using System.IO;
using System.Linq;
using Arkadia.Ingestion;
using Avalonia.Controls;
using Avalonia.Threading;
using Xunit;

namespace Arkadia.Tests.Ingestion;

[CollectionDefinition("AvaloniaDispatcher", DisableParallelization = true)]
public sealed class AvaloniaDispatcherCollection { }

/// <summary>
/// DAT-NAMING-2 / FIX-1 regression: the Import DAT dialog's Display Name must follow Authority/Variant until the
/// user edits it. The Window itself cannot be built without a windowing platform, so these tests drive the REAL
/// <see cref="SingleDatImportIdentityBinding"/> with REAL <see cref="TextBox"/> controls, wired to
/// <c>TextChanged</c> exactly as the dialog's AXAML does, and pump the dispatcher to deliver the (deferred)
/// notifications the way the running application receives them.
/// </summary>
[Collection("AvaloniaDispatcher")]
public sealed class ImportDatIdentityBindingTests
{
    private sealed class Harness
    {
        public SingleDatImportForm Form { get; }
        public TextBox VariantBox { get; } = new();
        public TextBox IdBox { get; } = new();
        public TextBox DisplayNameBox { get; } = new();
        public SingleDatImportIdentityBinding Binding { get; }

        public Harness(params string[] existingIds)
        {
            // Mirrors the dialog: the AXAML sets Text="main" on the variant box BEFORE the code-behind exists.
            VariantBox.Text = "main";

            Form    = new SingleDatImportForm(existingIds);
            Binding = new SingleDatImportIdentityBinding(Form, VariantBox, IdBox, DisplayNameBox);

            // Same event wiring as ImportDatDialog.axaml (TextChanged="OnVariantChanged"/"OnDisplayNameChanged").
            VariantBox.TextChanged     += (_, _) => Binding.OnVariantTextChanged();
            DisplayNameBox.TextChanged += (_, _) => Binding.OnDisplayNameTextChanged();

            // Mirrors the constructor: default selections fire OnSelectionChanged (Authority defaults to Custom).
            SelectAuthority("custom", "Custom");
            Pump();
        }

        private string? _system = "nds", _authority, _authorityName, _media = "cartridge";

        public void SelectAuthority(string id, string name)
        {
            _authority = id; _authorityName = name;
            Binding.OnSelectionChanged(_system, _authority, _authorityName, _media);
        }

        public void SelectMedia(string id) { _media = id; Binding.OnSelectionChanged(_system, _authority, _authorityName, _media); }
        public void SelectSystem(string id) { _system = id; Binding.OnSelectionChanged(_system, _authority, _authorityName, _media); }

        /// <summary>Delivers any queued TextChanged notifications (what the running dispatcher does after the handler returns).</summary>
        public void Pump() => Dispatcher.UIThread.RunJobs();

        /// <summary>A user keystroke-level edit: sets the box text and lets the notification arrive.</summary>
        public void UserTypesDisplayName(string text) { DisplayNameBox.Text = text; Pump(); }
        public void UserTypesVariant(string text)     { VariantBox.Text = text;     Pump(); }
    }

    // ── Root cause characterization ─────────────────────────────────────────────

    [Fact]
    public void Characterization_TextChangedIsDeliveredDeferred_NotInsideTheTextAssignment()
    {
        // The reason a boolean "syncing" flag around `Text = ...` cannot work in Avalonia 11.3.
        var box = new TextBox();
        int fired = 0;
        box.TextChanged += (_, _) => fired++;

        box.Text = "x";
        Assert.Equal(0, fired);          // not delivered synchronously
        Dispatcher.UIThread.RunJobs();
        Assert.True(fired >= 1);         // delivered later
    }

    // ── 1. Initialization ───────────────────────────────────────────────────────

    [Fact]
    public void Init_DefaultCustomAuthority_SuggestsDisplayNameAndId()
    {
        var h = new Harness();
        Assert.Equal("Custom - Main", h.DisplayNameBox.Text);
        Assert.Equal("nds-custom-cartridge-main", h.IdBox.Text);
        Assert.False(h.Form.DisplayNameEdited);
        Assert.True(h.Form.IsValid);
    }

    // ── 2. The reported bug: Custom → NoIntro ───────────────────────────────────

    [Fact]
    public void SwitchingAuthority_RegeneratesTheSuggestion_WhenNotManuallyEdited()
    {
        var h = new Harness();
        Assert.Equal("Custom - Main", h.DisplayNameBox.Text);

        h.SelectAuthority("nointro", "NoIntro");
        h.Pump();

        Assert.Equal("NoIntro - Main", h.DisplayNameBox.Text);
        Assert.Equal("NoIntro - Main", h.Form.DisplayName);
        Assert.Equal("nds-nointro-cartridge-main", h.IdBox.Text);
        Assert.False(h.Form.DisplayNameEdited);
    }

    [Fact]
    public void SwitchingAuthority_WorksWithoutPumpingBetweenSteps_QueuedNotificationsAreHarmless()
    {
        var h = new Harness();
        h.SelectAuthority("nointro", "NoIntro");      // programmatic write, notification still queued
        h.SelectAuthority("redump", "ReDump");        // second programmatic write before any delivery
        h.Pump();                                     // both late notifications now arrive

        Assert.Equal("ReDump - Main", h.DisplayNameBox.Text);
        Assert.False(h.Form.DisplayNameEdited);
        Assert.Equal("nds-redump-cartridge-main", h.IdBox.Text);
    }

    // ── 3. Variant ──────────────────────────────────────────────────────────────

    [Fact]
    public void ChangingVariant_RegeneratesTheSuggestion_WhenNotManuallyEdited()
    {
        var h = new Harness();
        h.SelectAuthority("nointro", "NoIntro"); h.Pump();

        h.UserTypesVariant("downloadplay");

        Assert.Equal("nds-nointro-cartridge-downloadplay", h.IdBox.Text);
        Assert.Equal("NoIntro - Downloadplay", h.DisplayNameBox.Text);
        Assert.False(h.Form.DisplayNameEdited);
        Assert.Equal("downloadplay", h.Form.Variant);
    }

    [Fact]
    public void ChangingVariantWithHyphen_TitleCasesTheParts()
    {
        var h = new Harness();
        h.SelectAuthority("local", "Local"); h.Pump();
        h.UserTypesVariant("download-play");
        Assert.Equal("Local - Download Play", h.DisplayNameBox.Text);
    }

    // ── 4. Several changes before any manual edit ───────────────────────────────

    [Fact]
    public void ManyAuthorityAndVariantChanges_BeforeManualEditing_AlwaysFollowTheSuggestion()
    {
        var h = new Harness();
        foreach (var (id, name) in new[] { ("nointro", "NoIntro"), ("redump", "ReDump"), ("tosec", "TOSEC"), ("custom", "Custom") })
        {
            h.SelectAuthority(id, name);
            h.Pump();
            Assert.Equal($"{name} - Main", h.DisplayNameBox.Text);
            Assert.False(h.Form.DisplayNameEdited);
        }
        h.UserTypesVariant("sbi");
        Assert.Equal("Custom - Sbi", h.DisplayNameBox.Text);
        h.SelectAuthority("nointro", "NoIntro"); h.Pump();
        Assert.Equal("NoIntro - Sbi", h.DisplayNameBox.Text);
        Assert.False(h.Form.DisplayNameEdited);
    }

    // ── 5/6. Manual edit is preserved ───────────────────────────────────────────

    [Fact]
    public void ManualEdit_ThenAuthorityChange_IsPreserved()
    {
        var h = new Harness();
        h.UserTypesDisplayName("My Own Name");
        Assert.True(h.Form.DisplayNameEdited);

        h.SelectAuthority("nointro", "NoIntro"); h.Pump();

        Assert.Equal("My Own Name", h.DisplayNameBox.Text);
        Assert.Equal("My Own Name", h.Form.DisplayName);
        Assert.Equal("nds-nointro-cartridge-main", h.IdBox.Text);     // id still follows the selection
    }

    [Fact]
    public void ManualEdit_ThenVariantChange_IsPreserved()
    {
        var h = new Harness();
        h.SelectAuthority("nointro", "NoIntro"); h.Pump();
        h.UserTypesDisplayName("Curated Set");

        h.UserTypesVariant("downloadplay");

        Assert.Equal("Curated Set", h.DisplayNameBox.Text);
        Assert.Equal("nds-nointro-cartridge-downloadplay", h.IdBox.Text);
    }

    [Fact]
    public void ManualEdit_SurvivesManyLaterChanges_InAnyOrder()
    {
        var h = new Harness();
        h.UserTypesDisplayName("Keep Me");
        h.SelectAuthority("redump", "ReDump");
        h.UserTypesVariant("a");
        h.SelectMedia("other");
        h.SelectSystem("psx");
        h.SelectAuthority("tosec", "TOSEC");
        h.Pump();

        Assert.Equal("Keep Me", h.DisplayNameBox.Text);
        Assert.Equal("psx-tosec-other-a", h.IdBox.Text);
    }

    [Fact]
    public void ManualEdit_PartiallyDeletingTheSuggestion_CountsAsAnEdit()
    {
        var h = new Harness();
        h.SelectAuthority("nointro", "NoIntro"); h.Pump();
        h.UserTypesDisplayName("NoIntro - Mai");           // user trimmed the suggestion
        h.SelectAuthority("redump", "ReDump"); h.Pump();
        Assert.Equal("NoIntro - Mai", h.DisplayNameBox.Text);
    }

    // ── 7. Programmatic updates are never user edits ────────────────────────────

    [Fact]
    public void ProgrammaticUpdates_NeverMarkTheFieldAsEdited()
    {
        var h = new Harness();
        Assert.False(h.Form.DisplayNameEdited);

        h.SelectAuthority("nointro", "NoIntro");
        h.SelectMedia("other");
        h.SelectSystem("psx");
        h.Pump();
        Assert.False(h.Form.DisplayNameEdited);

        h.UserTypesVariant("main");                         // same value: still not a display-name edit
        Assert.False(h.Form.DisplayNameEdited);
    }

    [Fact]
    public void ReTypingTheIdenticalSuggestedText_IsNotRecordedAsAnEdit()
    {
        var h = new Harness();
        h.UserTypesDisplayName(h.Form.DisplayName);
        Assert.False(h.Form.DisplayNameEdited);
        h.SelectAuthority("nointro", "NoIntro"); h.Pump();
        Assert.Equal("NoIntro - Main", h.DisplayNameBox.Text);
    }

    // ── 8. Validation + id independence still hold ──────────────────────────────

    [Fact]
    public void DisplayNameValidation_StillBlocks_AndTheIdIsIndependentOfIt()
    {
        var h = new Harness();
        h.SelectAuthority("nointro", "NoIntro"); h.Pump();
        var idBefore = h.IdBox.Text;

        h.UserTypesDisplayName("   ");
        Assert.False(h.Form.IsValid);
        Assert.Contains("Display Name", h.Form.FirstError);
        Assert.Equal(idBefore, h.IdBox.Text);                // blank name does not touch the id

        h.UserTypesDisplayName("Anything Goes Here");
        Assert.True(h.Form.IsValid);
        Assert.Equal(idBefore, h.IdBox.Text);
        Assert.Equal("nds-nointro-cartridge-main", h.Form.FinalId);
    }

    [Fact]
    public void InvalidVariant_StillBlocks_IsNeverRewritten_AndLeavesTheSuggestionFollowingIt()
    {
        var h = new Harness();
        h.SelectAuthority("nointro", "NoIntro"); h.Pump();

        h.UserTypesVariant("Bad Value");

        Assert.False(h.Form.IsValid);
        Assert.Equal("Bad Value", h.VariantBox.Text);        // box untouched
        Assert.Equal("Bad Value", h.Form.Variant);           // form holds it verbatim
        Assert.Null(h.Form.FinalId);
        Assert.Equal("nds-nointro-cartridge-Bad Value", h.IdBox.Text);   // attempted composition, flagged invalid
    }

    [Fact]
    public void Collision_StillBlocks()
    {
        var h = new Harness("NDS-NOINTRO-CARTRIDGE-MAIN");
        h.SelectAuthority("nointro", "NoIntro"); h.Pump();
        Assert.False(h.Form.IsValid);
        h.UserTypesVariant("extra");
        Assert.True(h.Form.IsValid);
        Assert.Equal("nds-nointro-cartridge-extra", h.Form.FinalId);
    }

    [Fact]
    public void Id_IsTheSameWhateverTheDisplayNameIs()
    {
        var a = new Harness(); a.SelectAuthority("nointro", "NoIntro"); a.Pump();
        var b = new Harness(); b.SelectAuthority("nointro", "NoIntro"); b.Pump();
        b.UserTypesDisplayName("Completely Different");
        Assert.Equal(a.Form.FinalId, b.Form.FinalId);
    }

    // ── Wiring pins (the Window cannot be constructed headless) ─────────────────

    [Fact]
    public void Dialog_DelegatesToTheBinding_AndNoLongerUsesTheTimingDependentFlag()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Arkadia.sln"))) root = root.Parent;
        var code = File.ReadAllText(Path.Combine(root!.FullName, "Ingestion", "ImportDatDialog.axaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root.FullName, "Ingestion", "ImportDatDialog.axaml"));

        Assert.DoesNotContain("_syncingUi", code);
        Assert.Contains("_identity.OnDisplayNameTextChanged()", code);
        Assert.Contains("_identity.OnVariantTextChanged()", code);
        Assert.Contains("_identity.OnSelectionChanged(", code);
        Assert.DoesNotContain("SetDisplayNameFromUser", code);          // only the binding decides user-edit vs echo
        Assert.Contains("TextChanged=\"OnVariantChanged\"", xaml);
        Assert.Contains("TextChanged=\"OnDisplayNameChanged\"", xaml);
        Assert.Contains("x:Name=\"DisplayNameInput\"", xaml);
    }
}
