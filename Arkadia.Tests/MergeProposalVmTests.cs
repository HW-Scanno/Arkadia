using System;
using System.Collections.Generic;
using Arkadia;
using Arkadia.Data;
using Avalonia.Media;
using Xunit;

namespace Arkadia.Tests;

public sealed class MergeProposalVmTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProposalRowVm LockedVm() => new()
    {
        FieldKey      = "title",
        DisplayName   = "Title",
        CurrentValue  = "Manual Title",
        ProviderValue = "Provider Title",
        Provider      = "screenscraper",
        CanSelect     = false,
        CanOverride   = true,
        StatusLabel   = "LOCKED",
        StatusBrush   = new SolidColorBrush(Color.Parse("#FFA726")),
    };

    private static ProposalRowVm ManualVm() => new()
    {
        FieldKey      = "developer",
        DisplayName   = "Developer",
        CurrentValue  = "My Dev",
        ProviderValue = "Sega",
        Provider      = "screenscraper",
        CanSelect     = false,
        CanOverride   = true,
        StatusLabel   = "MANUAL",
        StatusBrush   = new SolidColorBrush(Color.Parse("#FFD54F")),
    };

    private static ProposalRowVm NewVm() => new()
    {
        FieldKey      = "genre",
        DisplayName   = "Genre",
        CurrentValue  = "",
        ProviderValue = "Action",
        Provider      = "screenscraper",
        CanSelect     = true,
        CanOverride   = false,
        IsSelected    = true,
        StatusLabel   = "NEW",
        StatusBrush   = new SolidColorBrush(Color.Parse("#9FA4FF")),
    };

    private static ProposalRowVm SameVm() => new()
    {
        FieldKey      = "year",
        DisplayName   = "Year",
        CurrentValue  = "1994",
        ProviderValue = "1994",
        Provider      = "screenscraper",
        CanSelect     = false,
        CanOverride   = false,
        StatusLabel   = "SAME",
        StatusBrush   = new SolidColorBrush(Color.Parse("#4CAF50")),
    };

    // ── CanSelectEffective ────────────────────────────────────────────────────

    [Fact]
    public void LockedRow_CanSelectEffective_FalseByDefault()
        => Assert.False(LockedVm().CanSelectEffective);

    [Fact]
    public void LockedRow_AfterOverride_CanSelectEffective_True()
    {
        var vm = LockedVm();
        vm.IsOverridden = true;
        Assert.True(vm.CanSelectEffective);
    }

    [Fact]
    public void LockedRow_AfterOverrideThenUnoverride_CanSelectEffective_False()
    {
        var vm = LockedVm();
        vm.IsOverridden = true;
        vm.IsOverridden = false;
        Assert.False(vm.CanSelectEffective);
    }

    [Fact]
    public void ManualRow_CanSelectEffective_FalseByDefault()
        => Assert.False(ManualVm().CanSelectEffective);

    [Fact]
    public void NewRow_CanSelectEffective_TrueByDefault()
        => Assert.True(NewVm().CanSelectEffective);

    [Fact]
    public void SameRow_CanSelectEffective_FalseAndCannotOverride()
    {
        var vm = SameVm();
        Assert.False(vm.CanSelectEffective);
        Assert.False(vm.CanOverride);
    }

    // ── EffectiveStatusLabel ──────────────────────────────────────────────────

    [Fact]
    public void LockedRow_BeforeOverride_StatusLabel_IsLocked()
        => Assert.Equal("LOCKED", LockedVm().EffectiveStatusLabel);

    [Fact]
    public void LockedRow_AfterOverride_StatusLabel_IsOverride()
    {
        var vm = LockedVm();
        vm.IsOverridden = true;
        Assert.Equal("OVERRIDE", vm.EffectiveStatusLabel);
    }

    [Fact]
    public void ManualRow_AfterOverride_StatusLabel_IsOverride()
    {
        var vm = ManualVm();
        vm.IsOverridden = true;
        Assert.Equal("OVERRIDE", vm.EffectiveStatusLabel);
    }

    // ── PropertyChanged notifications ─────────────────────────────────────────

    [Fact]
    public void IsOverridden_SetTrue_FiresAllDependentNotifications()
    {
        var vm      = LockedVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.IsOverridden = true;

        Assert.Contains(nameof(ProposalRowVm.IsOverridden),         changed);
        Assert.Contains(nameof(ProposalRowVm.CanSelectEffective),   changed);
        Assert.Contains(nameof(ProposalRowVm.EffectiveStatusLabel), changed);
        Assert.Contains(nameof(ProposalRowVm.EffectiveStatusBrush), changed);
    }

    [Fact]
    public void IsOverridden_SetSameValue_DoesNotFirePropertyChanged()
    {
        var vm      = LockedVm();
        var count   = 0;
        vm.PropertyChanged += (_, _) => count++;

        vm.IsOverridden = false; // already false
        Assert.Equal(0, count);
    }

    // ── BuildRows CanOverride assignment ──────────────────────────────────────

    private static MetadataFieldStateRecord MakeState(string field, string source, bool locked) =>
        new("rel-001", field, source, source == "manual" ? "" : source, locked, "");

    [Fact]
    public void BuildRows_LockedField_CanOverrideTrue_CanSelectFalse()
    {
        var current   = new ReleaseMetadataRecord { ReleaseId = "r", Title = "Old", ScrapedAtUtc = "" };
        var states    = new Dictionary<string, MetadataFieldStateRecord>(StringComparer.Ordinal)
        {
            ["title"] = MakeState("title", "screenscraper", locked: true),
        };
        var proposals = new List<MetadataProposalRecord>
        {
            new("r", "ss", "title", "New", "", Accepted: false),
        };

        var rows = MergeMetadataDialog.BuildRows(current, states, proposals);

        Assert.Single(rows);
        Assert.True(rows[0].CanOverride);
        Assert.False(rows[0].CanSelect);
        Assert.Equal("LOCKED", rows[0].StatusLabel);
    }

    [Fact]
    public void BuildRows_ManualField_CanOverrideTrue_CanSelectFalse()
    {
        var current   = new ReleaseMetadataRecord { ReleaseId = "r", Developer = "Dev Co", ScrapedAtUtc = "" };
        var states    = new Dictionary<string, MetadataFieldStateRecord>(StringComparer.Ordinal)
        {
            ["developer"] = MakeState("developer", "manual", locked: false),
        };
        var proposals = new List<MetadataProposalRecord>
        {
            new("r", "ss", "developer", "Sega", "", Accepted: false),
        };

        var rows = MergeMetadataDialog.BuildRows(current, states, proposals);

        Assert.Single(rows);
        Assert.True(rows[0].CanOverride);
        Assert.False(rows[0].CanSelect);
        Assert.Equal("MANUAL", rows[0].StatusLabel);
    }

    [Fact]
    public void BuildRows_NewField_CanOverrideFalse_CanSelectTrue()
    {
        var current   = new ReleaseMetadataRecord { ReleaseId = "r", ScrapedAtUtc = "" };
        var states    = new Dictionary<string, MetadataFieldStateRecord>(StringComparer.Ordinal);
        var proposals = new List<MetadataProposalRecord>
        {
            new("r", "ss", "genre", "Action", "", Accepted: false),
        };

        var rows = MergeMetadataDialog.BuildRows(current, states, proposals);

        Assert.Single(rows);
        Assert.False(rows[0].CanOverride);
        Assert.True(rows[0].CanSelect);
        Assert.Equal("NEW", rows[0].StatusLabel);
    }

    [Fact]
    public void BuildRows_SameField_CanOverrideFalse_CanSelectFalse()
    {
        var current   = new ReleaseMetadataRecord { ReleaseId = "r", Year = "1994", ScrapedAtUtc = "" };
        var states    = new Dictionary<string, MetadataFieldStateRecord>(StringComparer.Ordinal);
        var proposals = new List<MetadataProposalRecord>
        {
            new("r", "ss", "year", "1994", "", Accepted: false),
        };

        var rows = MergeMetadataDialog.BuildRows(current, states, proposals);

        Assert.Single(rows);
        Assert.False(rows[0].CanOverride);
        Assert.False(rows[0].CanSelect);
        Assert.Equal("SAME", rows[0].StatusLabel);
    }
}
