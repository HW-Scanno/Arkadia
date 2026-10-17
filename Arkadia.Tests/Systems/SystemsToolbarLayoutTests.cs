using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Arkadia.Tests.Systems;

/// <summary>
/// PLATFORMS-TOOLBAR-1: structure of the Systems action bar. MainWindow cannot be instantiated without a windowing
/// platform, so the AXAML is parsed as XML (robust to whitespace/formatting) and the code-behind is inspected for
/// the handlers / enable rules, which this UI-only task must leave untouched. A successful Release build is the
/// AXAML-compilation check (compiled bindings are on).
/// </summary>
public sealed class SystemsToolbarLayoutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, rel);
    }

    private static readonly XDocument Axaml = XDocument.Load(RepoFile("MainWindow.axaml"));
    private static readonly string Code = File.ReadAllText(RepoFile("MainWindow.axaml.cs"));

    private static string? Name(XElement e) => (string?)e.Attribute(X + "Name");

    /// <summary>The panel that holds the Systems action bar (identified by its Create System button).</summary>
    private static XElement Bar() =>
        Axaml.Descendants().First(e => Name(e) == "SysActCreatePlatform").Parent!;

    private static XElement Btn(string xName) => Axaml.Descendants().First(e => Name(e) == xName);

    private static bool IsSeparator(XElement e) => e.Name.LocalName == "Border" && (string?)e.Attribute("Width") == "1";

    // ── Order and labels ────────────────────────────────────────────────────────

    [Fact]
    public void Toolbar_PresentsTheApprovedSequence_InOrder()
    {
        var kids = Bar().Elements().ToList();

        string Describe(XElement e) =>
            IsSeparator(e) ? "|" : e.Name.LocalName == "TextBlock" ? $"label:{(string?)e.Attribute("Text")}"
                                                                   : $"btn:{(string?)e.Attribute("Content")}";

        var expected = new[]
        {
            "label:System", "btn:Create", "btn:Edit", "btn:Delete",
            "|",
            "label:DAT", "btn:Import", "btn:Group", "btn:Configure", "btn:Update", "btn:Verify", "btn:Verify All",
            "btn:Delete", "btn:Delete Group",
        };

        Assert.Equal(expected, kids.Take(expected.Length).Select(Describe));
    }

    [Fact]
    public void UpdateAppearsBeforeVerify()
    {
        var names = Bar().Elements().Select(Name).Where(n => n is not null).ToList();
        Assert.True(names.IndexOf("SysActUpdateDat") < names.IndexOf("SysActVerifyAll"));
        Assert.True(names.IndexOf("SysActVerifyAll") < names.IndexOf("SysActVerifyDat"));   // Verify, then Verify All
    }

    [Fact]
    public void ThereIsExactlyOneSeparatorBetweenTheSystemAndDatGroups()
    {
        var kids = Bar().Elements().ToList();
        var sepIdx = kids.FindIndex(IsSeparator);
        var datLabelIdx = kids.FindIndex(e => Name(e) == "SysBarDatLabel");
        var sysDeleteIdx = kids.FindIndex(e => Name(e) == "SysActDeletePlatform");
        Assert.Equal(sysDeleteIdx + 1, sepIdx);
        Assert.Equal(sepIdx + 1, datLabelIdx);
    }

    // ── Section labels are not controls ─────────────────────────────────────────

    [Theory]
    [InlineData("SysBarSystemLabel", "System")]
    [InlineData("SysBarDatLabel", "DAT")]
    public void SectionLabels_ArePlainTextBlocks_NotButtons(string xName, string text)
    {
        var label = Btn(xName);
        Assert.Equal("TextBlock", label.Name.LocalName);
        Assert.Equal(text, (string?)label.Attribute("Text"));
        Assert.Null(label.Attribute("Click"));
        Assert.DoesNotContain("view-toggle", (string?)label.Attribute("Classes") ?? "");
        Assert.Contains("toolbar-section", (string?)label.Attribute("Classes") ?? "");
    }

    [Fact]
    public void SectionLabelStyle_IsNotHitTestable_NotFocusable_AndHasNoClickableAppearance()
    {
        var style = Axaml.Descendants().First(e => e.Name.LocalName == "Style"
                                                 && (string?)e.Attribute("Selector") == "TextBlock.toolbar-section");
        string? Val(string prop) => style.Elements().FirstOrDefault(s => (string?)s.Attribute("Property") == prop)?.Attribute("Value")?.Value;

        Assert.Equal("False", Val("IsHitTestVisible"));
        Assert.Equal("False", Val("Focusable"));
        Assert.Null(Val("Cursor"));
        Assert.Null(Val("Background"));
        Assert.Null(Val("BorderBrush"));
    }

    // ── Handlers preserved ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("SysActCreatePlatform",   "OnCreatePlatform")]
    [InlineData("SysActEditPlatform",     "OnEditPlatform")]
    [InlineData("SysActDeletePlatform",   "OnDeletePlatform")]
    [InlineData("SysActImportDat",        "OnImportDat")]
    [InlineData("SysActPreviewGroupDat",  "OnPreviewGroupDat")]
    [InlineData("SysActConfigureDat",     "OnSysConfigureDat")]
    [InlineData("SysActUpdateDat",        "OnSysUpdateDat")]
    [InlineData("SysActVerifyAll",        "OnSysVerify")]          // x:Name is historical: this is the contextual "Verify"
    [InlineData("SysActVerifyDat",        "OnSysVerifyDat")]       // ...and this one is "Verify All"
    [InlineData("SysActDeleteDat",        "OnSysDeleteDat")]
    [InlineData("SysActDeleteGroup",      "OnSysDeleteGroup")]
    public void EachCommand_KeepsItsOriginalHandler_AndTheHandlerStillExists(string xName, string handler)
    {
        Assert.Equal(handler, (string?)Btn(xName).Attribute("Click"));
        Assert.Matches(new Regex($@"void\s+{handler}\s*\("), Code);
    }

    [Theory]
    [InlineData("SysActCreatePlatform", "Create")]
    [InlineData("SysActEditPlatform",   "Edit")]
    [InlineData("SysActDeletePlatform", "Delete")]
    [InlineData("SysActImportDat",      "Import")]
    [InlineData("SysActPreviewGroupDat","Group")]
    [InlineData("SysActConfigureDat",   "Configure")]
    [InlineData("SysActUpdateDat",      "Update")]
    [InlineData("SysActVerifyAll",      "Verify")]
    [InlineData("SysActVerifyDat",      "Verify All")]
    [InlineData("SysActDeleteDat",      "Delete")]
    [InlineData("SysActDeleteGroup",    "Delete Group")]
    public void ButtonLabels_AreTheShortApprovedOnes(string xName, string label)
        => Assert.Equal(label, (string?)Btn(xName).Attribute("Content"));

    [Theory]
    [InlineData("SysActCreatePlatform", "Create System")]
    [InlineData("SysActEditPlatform",   "Edit System")]
    [InlineData("SysActDeletePlatform", "Delete System")]
    [InlineData("SysActImportDat",      "Import DAT")]
    [InlineData("SysActPreviewGroupDat","Create Group DAT")]
    [InlineData("SysActConfigureDat",   "Configure DAT")]
    [InlineData("SysActUpdateDat",      "Update DAT")]
    [InlineData("SysActDeleteDat",      "Delete DAT")]
    [InlineData("SysActDeleteGroup",    "Delete Group")]
    public void ShortenedButtons_KeepTheirFullAccessibleName_AndHaveATooltip(string xName, string fullName)
    {
        var b = Btn(xName);
        Assert.Equal(fullName, b.Attributes().First(a => a.Name.LocalName == "AutomationProperties.Name").Value);
        Assert.False(string.IsNullOrWhiteSpace(b.Attributes().FirstOrDefault(a => a.Name.LocalName == "ToolTip.Tip")?.Value));
    }

    // ── Styling ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SysActDeletePlatform", true)]
    [InlineData("SysActDeleteDat",      true)]
    [InlineData("SysActDeleteGroup",    true)]
    [InlineData("SysActCreatePlatform", false)]
    [InlineData("SysActEditPlatform",   false)]
    [InlineData("SysActImportDat",      false)]
    [InlineData("SysActPreviewGroupDat",false)]
    [InlineData("SysActConfigureDat",   false)]
    [InlineData("SysActUpdateDat",      false)]
    [InlineData("SysActVerifyAll",      false)]
    [InlineData("SysActVerifyDat",      false)]
    public void DestructiveButtons_KeepDangerStyling_OthersStayNeutral(string xName, bool danger)
    {
        var classes = ((string?)Btn(xName).Attribute("Classes") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("view-toggle", classes);
        Assert.Equal(danger, classes.Contains("danger"));
    }

    [Fact]
    public void DangerStyle_IsStillDefined()
    {
        var src = File.ReadAllText(RepoFile("MainWindow.axaml"));
        Assert.Contains("Button.view-toggle.danger", src);
    }

    [Fact]
    public void Bar_IsASingleHorizontalRow_WithAnOverflowButton_AndNoWrapScrollOrClipping()
    {
        var items = Bar();
        Assert.Equal("SysBarItems", Name(items));
        Assert.Equal("StackPanel", items.Name.LocalName);
        Assert.Equal("Horizontal", (string?)items.Attribute("Orientation"));
        Assert.Equal("True", (string?)items.Attribute("ClipToBounds"));          // a transient frame can never paint outside the row

        var host = items.Parent!;
        Assert.Equal("SysBarHost", Name(host));
        Assert.Equal("DockPanel", host.Name.LocalName);

        var chevron = Btn("SysActOverflow");
        Assert.Same(host, chevron.Parent);                                        // sibling of the row, not inside it
        Assert.Equal("Right", chevron.Attributes().First(a => a.Name.LocalName == "DockPanel.Dock").Value);
        Assert.Equal("False", (string?)chevron.Attribute("IsVisible"));          // shown by the controller only on overflow
        Assert.Equal("More commands", chevron.Attributes().First(a => a.Name.LocalName == "AutomationProperties.Name").Value);
        Assert.Null(chevron.Attribute("Click"));                                  // opens its flyout; no handler of its own

        var barRegion = host.DescendantsAndSelf().Select(e => e.Name.LocalName).ToList();
        Assert.DoesNotContain("WrapPanel", barRegion);
        Assert.DoesNotContain("ScrollViewer", barRegion);
    }

    [Fact]
    public void MainWindow_WiresTheOverflowController_ToTheBarParts()
    {
        Assert.Contains("new Systems.ToolbarOverflowController(SysBarItems, SysActOverflow", Code);
        Assert.Contains("_systemsToolbar.Attach(SysBarHost)", Code);
    }

    [Fact]
    public void BarControlNames_AreUnique()
    {
        var names = Axaml.Descendants().Select(Name).Where(n => n is not null).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // ── Enable/disable rules untouched ──────────────────────────────────────────

    [Theory]
    [InlineData("SysActEditPlatform.IsEnabled   = hasPlatform;")]
    [InlineData("SysActDeletePlatform.IsEnabled = hasPlatform;")]
    [InlineData("SysActConfigureDat.IsEnabled   = datHasStore || hasGroup;")]
    [InlineData("SysActUpdateDat.IsEnabled     = hasDat;")]
    [InlineData("SysActVerifyDat.IsEnabled     = true;")]
    [InlineData("SysActVerifyAll.IsEnabled     = datHasStore || hasGroup;")]
    [InlineData("SysActDeleteDat.IsEnabled     = hasDat;")]
    [InlineData("SysActDeleteGroup.IsEnabled   = hasGroup;")]
    public void EnableRules_AreUnchanged(string assignment)
        => Assert.Contains(assignment, Code);

    // ── Rest of the bar and the Group workflow untouched ────────────────────────

    [Fact]
    public void OtherCommandGroups_AreStillPresent_AfterTheDatGroup()
    {
        var names = Bar().Descendants().Concat(Bar().Elements()).Select(Name).Where(n => n is not null).ToList();
        Assert.Contains("SysActIngestFiles", names);
        Assert.Contains("SysActVerifyArchive", names);
        Assert.Equal("OnSysIngestFiles",   (string?)Btn("SysActIngestFiles").Attribute("Click"));
        Assert.Equal("OnSysVerifyArchive", (string?)Btn("SysActVerifyArchive").Attribute("Click"));
        Assert.Contains(Bar().Elements(), e => (string?)e.Attribute("Click") == "OnOpenDatDownloader");
    }

    [Fact]
    public void GroupWorkflow_HandlersAreWiredToTheSameCommands_AndNotRelabelledInCode()
    {
        Assert.Equal("OnPreviewGroupDat", (string?)Btn("SysActPreviewGroupDat").Attribute("Click"));
        Assert.Equal("OnSysDeleteGroup",  (string?)Btn("SysActDeleteGroup").Attribute("Click"));
        // No code-behind rewrites button captions at runtime.
        Assert.DoesNotMatch(new Regex(@"SysAct\w+\.Content\s*="), Code);
    }
}
