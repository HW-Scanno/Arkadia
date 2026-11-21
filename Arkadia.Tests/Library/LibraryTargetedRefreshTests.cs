using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Arkadia.Analytics;
using Arkadia.Data;
using Arkadia.Library;
using Xunit;

namespace Arkadia.Tests.Library;

/// <summary>
/// PERFORMANCE-01 — fast release marking. After a single release changes Wanted/Unwanted state the Library reloads
/// ONLY the affected leaf (<see cref="LibraryDatasetBuilder.TryRefreshLeaf"/>) instead of every DAT line, and
/// Analytics is invalidated instead of recollected while hidden.
///
/// <para>The correctness reference throughout is a complete rebuild (<see cref="LibraryDatasetBuilder.BuildAll"/>)
/// taken after the same mutation: the targeted result must be indistinguishable from it. Real
/// <see cref="CatalogService"/> + real leaf databases over a temp directory — never the runtime data.</para>
/// </summary>
public sealed class LibraryTargetedRefreshTests : IDisposable
{
    private const string SinglePsx  = "psx-redump-cd";
    private const string SingleC64  = "c64-nointro-cart";
    private const string GroupLeafA = "c64-tosec-games-d64";
    private const string GroupLeafB = "c64-tosec-demos-d64";
    private const string GroupLeafC = "c64-tosec-apps-d64";

    private static readonly string[] AllLeafIds = [SinglePsx, SingleC64, GroupLeafA, GroupLeafB, GroupLeafC];
    private static readonly HashSet<string> GroupLeafIds =
        new(StringComparer.OrdinalIgnoreCase) { GroupLeafA, GroupLeafB, GroupLeafC };

    private readonly string         _dir;
    private readonly string         _dataDir;
    private readonly CatalogService _catalog;

    public LibraryTargetedRefreshTests()
    {
        _dir     = Path.Combine(Path.GetTempPath(), "ArkPerf01_" + Guid.NewGuid().ToString("N")[..8]);
        _dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_dataDir);
        _catalog = Seed();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static List<DatParser.ParsedGame> Games(string prefix, int n) =>
        Enumerable.Range(0, n).Select(i => new DatParser.ParsedGame
        {
            Name      = $"{prefix} Game {i:D2}",
            Region    = i % 2 == 0 ? "Europe" : "USA",
            Languages = i % 3 == 0 ? "en,fr" : "en",
            Roms      = new List<DatParser.ParsedRom>
            {
                new() { Name = $"{prefix}-{i:D2}.bin", Size = (1000 + i).ToString(), Sha1 = Sha1Of(prefix, i, 0) },
                new() { Name = $"{prefix}-{i:D2}.cue", Size = "120",                 Sha1 = Sha1Of(prefix, i, 1) },
            },
        }).ToList();

    private static string Sha1Of(string prefix, int i, int part)
        => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes($"{prefix}/{i}/{part}"))).ToLowerInvariant();

    private string DbPath(string datLineId) => Path.Combine(_dataDir, RelPath(datLineId));

    private static string RelPath(string datLineId)
        => $"systems/{(datLineId.StartsWith("psx", StringComparison.Ordinal) ? "psx" : "c64")}/{datLineId}.db";

    private DatLineStore Store(string datLineId) => new(DbPath(datLineId));

    /// <summary>
    /// Two systems; two Single DATs; one Group with three leaves. Mixed statuses, one Tier-A present release whose
    /// tier comes from its derived artifact, scraped metadata on some releases and a "catalog" title mode on one DAT.
    /// </summary>
    private CatalogService Seed()
    {
        var c = new CatalogService(_dataDir);
        c.SaveHardwareFamilies(new List<HardwareFamilyRecord>
        {
            new() { Id = "c64", Name = "Commodore 64", Manufacturer = "Commodore", HardwareTypeId = "" },
            new() { Id = "psx", Name = "PlayStation",  Manufacturer = "Sony",      HardwareTypeId = "" },
        });

        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        LeafDatDatabaseBuilder.Build(DbPath(SinglePsx), SinglePsx, Games("psx", 12), null, CancellationToken.None);
        LeafDatDatabaseBuilder.Build(DbPath(SingleC64), SingleC64, Games("cart", 8), null, CancellationToken.None);
        c.SaveDatLines(new List<DatLineRecord>
        {
            new()
            {
                Id = SinglePsx, HardwareFamilyId = "psx", Name = "cd", Authority = "redump", MediaTypeId = "cd",
                Version = "1", DataStorePath = RelPath(SinglePsx), ReleaseCount = 12, ImportedAtUtc = t0.AddMinutes(-1),
                LibraryTitleMode = "catalog",
            },
            new()
            {
                Id = SingleC64, HardwareFamilyId = "c64", Name = "cart", Authority = "nointro", MediaTypeId = "cartridge",
                Version = "1", DataStorePath = RelPath(SingleC64), ReleaseCount = 8, ImportedAtUtc = t0.AddMinutes(-2),
            },
        });

        var leaves = new List<GroupDatCatalogLeafCreate>();
        int n = 0;
        foreach (var id in new[] { GroupLeafA, GroupLeafB, GroupLeafC })
        {
            n++;
            LeafDatDatabaseBuilder.Build(DbPath(id), id, Games(id[10..], 6 + n), null, CancellationToken.None);
            leaves.Add(new GroupDatCatalogLeafCreate
            {
                DatLine = new DatLineRecord
                {
                    Id = id, HardwareFamilyId = "c64", Name = "floppy", Authority = "tosec", MediaTypeId = "floppy",
                    Version = "1", DataStorePath = RelPath(id), ReleaseCount = 6 + n, ImportedAtUtc = t0.AddMinutes(-10 - n),
                },
                RelativeDatPath = $"Set{n}/g.dat", SourceDatName = "g.dat",
                SourceDatSha256 = new string('a', 64), LastSeenGroupRevision = 0,
            });
        }
        c.CreateDatGroupWithLeaves(new GroupDatCatalogCreateRequest
        {
            GroupId = "c64-tosec", DisplayName = "Commodore 64 TOSEC", HardwareFamilyId = "c64",
            Authority = "tosec", Leaves = leaves,
        });

        // Every leaf: release 0 present + Tier A through its derived artifact; release 1 pending; release 2 already
        // unwanted+hidden; release 3 has scraped metadata. The rest stay missing.
        foreach (var id in AllLeafIds)
        {
            var store = Store(id);
            var rel   = store.LoadReleases();

            var cik = "sha1:" + Sha1Of(id, 0, 9);
            store.SaveReleaseContentLink(new ReleaseContentLinkRecord
            {
                Id = Guid.NewGuid().ToString("N"), ReleaseId = rel[0].Id, ContentIdentityKey = cik, CreatedAtUtc = t0,
            });
            store.IngestDerivedArtifact(cik, "", "chd", $"{id}-0.chd", $"archive/x/{id}/{id}-0.chd", 4096,
                                        Sha1Of(id, 0, 8), archiveTier: "A");
            store.UpdateReleaseStatus(rel[0].Id, "present");
            store.UpdateReleaseStatus(rel[1].Id, "pending");
            store.UpdateReleaseStatus(rel[2].Id, "unwanted");
            store.SetShowInCatalog(rel[2].Id, false);
            store.SaveReleaseMetadata(new ReleaseMetadataRecord
            {
                ReleaseId = rel[3].Id, Title = $"Scraped Title {id}", Developer = "Dev", Publisher = "Pub", Year = "1994",
            });
        }
        return c;
    }

    // ── Helpers mirroring MainWindow ──────────────────────────────────────────

    // Same shape as MainWindow.DatLineLabel: Group leaves keep "authority · MEDIA"; Single DATs use their display name.
    private static string Label(DatLineRecord dl)
        => GroupLeafIds.Contains(dl.Id)
            ? $"{dl.Authority} · {dl.MediaTypeId.ToUpperInvariant()}"
            : DatLineDisplayName.Resolve(dl.DisplayName, dl.Authority, dl.MediaTypeId);

    /// <summary>The complete rebuild — the correctness reference.</summary>
    private List<LibraryDataset> FullRebuild()
        => LibraryDatasetBuilder.BuildAll(_catalog.LoadPlatforms(), _catalog.LoadDatLines(), _dataDir, Label);

    /// <summary>The targeted single-leaf refresh, exactly as MainWindow.RefreshLibraryLeafAfterReleaseMutation calls it.</summary>
    private LibraryLeafRefresh? Targeted(List<LibraryDataset> datasets, string datLineId)
        => LibraryDatasetBuilder.TryRefreshLeaf(datasets, _catalog.LoadPlatforms(), _catalog.LoadDatLines(), datLineId, _dataDir, Label);

    private static LibraryDataset DatasetOf(IEnumerable<LibraryDataset> datasets, string datLineId)
        => LibraryDatasetSelector.ByDatLineId(datasets, datLineId)
           ?? throw new InvalidOperationException($"no dataset for {datLineId}");

    private static LibraryEntry Entry(IEnumerable<LibraryDataset> datasets, string datLineId, int index)
        => DatasetOf(datasets, datLineId).Entries[index];

    private void MarkUnwanted(LibraryEntry e)
    {
        // The exact statements of the "Mark as Unwanted" handler.
        var store = new DatLineStore(e.DbPath);
        store.UpdateReleaseStatus(e.ReleaseId, "unwanted");
        store.SetShowInCatalog(e.ReleaseId, false);
    }

    private static void RestoreWanted(LibraryEntry e)
        => new DatLineStore(e.DbPath).RestoreWantedRelease(e.ReleaseId);   // the "Restore Wanted" handler

    /// <summary>Every observable field of every entry of every dataset, in order — the equality used against the reference.</summary>
    private static string Snapshot(IEnumerable<LibraryDataset> datasets)
    {
        var sb = new StringBuilder();
        foreach (var d in datasets)
        {
            sb.Append("## ").Append(d.Platform).Append(" | ").Append(d.DatLine).Append(" | ").Append(d.Entries.Count).Append('\n');
            foreach (var e in d.Entries)
            {
                sb.AppendJoin('|',
                    e.ReleaseId, e.Name, e.DisplayName, e.Platform, e.HardwareFamilyId, e.Authority, e.Status,
                    e.ShowInCatalog, e.Region, e.Languages, e.Format, e.Size, e.Tier, e.TierDisplay, e.DbPath,
                    e.DatLineId, e.TransformStrategyType, e.IntroducedAtUtc?.ToString("O") ?? "-", e.IsNew,
                    JsonSerializer.Serialize(e.Metadata), JsonSerializer.Serialize(e.RomFiles));
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private void AssertEqualsFullRebuild(List<LibraryDataset> targeted)
        => Assert.Equal(Snapshot(FullRebuild()), Snapshot(targeted));

    // ── The fixture itself is meaningful ──────────────────────────────────────

    [Fact]
    public void Fixture_CoversStatusesTierMetadataAndBothDatKinds()
    {
        var all = FullRebuild();

        Assert.Equal(5, all.Count);
        Assert.Equal(new[] { "PlayStation", "Commodore 64" }, all.Select(d => d.Platform).Distinct());
        var psx = DatasetOf(all, SinglePsx).Entries;
        Assert.Equal(12, psx.Count);
        Assert.Contains(psx, e => e.Status == "Present"  && e.Tier == "A");    // tier resolved through the derived artifact
        Assert.Contains(psx, e => e.Status == "Pending");
        Assert.Contains(psx, e => e.Status == "Unwanted" && !e.ShowInCatalog);
        Assert.Contains(psx, e => e.Status == "Missing");
        Assert.Contains(psx, e => e.Metadata is not null && e.DisplayName == $"Scraped Title {SinglePsx}");   // "catalog" title mode
        Assert.All(psx, e => Assert.Equal(2, e.RomFiles.Count));
    }

    // ── Single DAT ────────────────────────────────────────────────────────────

    [Fact]
    public void SingleDat_WantedToUnwanted_MatchesFullRebuild()
    {
        var datasets = FullRebuild();
        var target   = DatasetOf(datasets, SinglePsx).Entries.First(e => e.Status == "Missing");

        MarkUnwanted(target);
        var refresh = Targeted(datasets, SinglePsx);

        Assert.NotNull(refresh);
        var after = DatasetOf(datasets, SinglePsx).Entries.Single(e => e.ReleaseId == target.ReleaseId);
        Assert.Equal("Unwanted", after.Status);
        Assert.False(after.ShowInCatalog);
        Assert.Equal("—", after.TierDisplay);
        AssertEqualsFullRebuild(datasets);
    }

    [Fact]
    public void SingleDat_UnwantedToWanted_MatchesFullRebuild()
    {
        var datasets = FullRebuild();
        var target   = DatasetOf(datasets, SinglePsx).Entries.First(e => e.Status == "Unwanted");

        RestoreWanted(target);
        Assert.NotNull(Targeted(datasets, SinglePsx));

        var after = DatasetOf(datasets, SinglePsx).Entries.Single(e => e.ReleaseId == target.ReleaseId);
        Assert.Equal("Missing", after.Status);      // Restore Wanted → missing + visible again
        Assert.True(after.ShowInCatalog);
        AssertEqualsFullRebuild(datasets);
    }

    [Fact]
    public void UnwantedIsACuratorVeto_AGenericStatusUpdateStaysUnwantedInBothBuilds()
    {
        var datasets = FullRebuild();
        var target   = DatasetOf(datasets, SinglePsx).Entries.First(e => e.Status == "Unwanted");

        new DatLineStore(target.DbPath).UpdateReleaseStatus(target.ReleaseId, "present");   // SQL-guarded: no effect
        Assert.NotNull(Targeted(datasets, SinglePsx));

        Assert.Equal("Unwanted", DatasetOf(datasets, SinglePsx).Entries.Single(e => e.ReleaseId == target.ReleaseId).Status);
        AssertEqualsFullRebuild(datasets);
    }

    // ── DAT Group leaves ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(GroupLeafA)]
    [InlineData(GroupLeafB)]
    [InlineData(GroupLeafC)]
    public void GroupLeaf_UnwantedThenRestored_MatchesFullRebuildAtEachStep(string leafId)
    {
        var datasets = FullRebuild();
        var target   = DatasetOf(datasets, leafId).Entries.First(e => e.Status == "Missing");

        MarkUnwanted(target);
        Assert.NotNull(Targeted(datasets, leafId));
        Assert.Equal("Unwanted", DatasetOf(datasets, leafId).Entries.Single(e => e.ReleaseId == target.ReleaseId).Status);
        AssertEqualsFullRebuild(datasets);

        RestoreWanted(target);
        Assert.NotNull(Targeted(datasets, leafId));
        Assert.Equal("Missing", DatasetOf(datasets, leafId).Entries.Single(e => e.ReleaseId == target.ReleaseId).Status);
        AssertEqualsFullRebuild(datasets);
    }

    [Fact]
    public void GroupLeaf_Refresh_LeavesSiblingLeavesWithTheSameLabelUntouched()
    {
        // All three leaves share the label "tosec · FLOPPY": identity must be the dat_line id, never the label.
        var datasets = FullRebuild();
        var siblingB = DatasetOf(datasets, GroupLeafB);
        var siblingC = DatasetOf(datasets, GroupLeafC);
        Assert.Equal(siblingB.DatLine, DatasetOf(datasets, GroupLeafA).DatLine);

        MarkUnwanted(Entry(datasets, GroupLeafA, 4));
        Assert.NotNull(Targeted(datasets, GroupLeafA));

        Assert.Same(siblingB, DatasetOf(datasets, GroupLeafB));
        Assert.Same(siblingC, DatasetOf(datasets, GroupLeafC));
        AssertEqualsFullRebuild(datasets);
    }

    // ── No reload of unrelated DAT lines ──────────────────────────────────────

    [Fact]
    public void TargetedRefresh_OpensExactlyOneLeafDatabase_AFullRebuildOpensEveryOne()
    {
        var datasets = FullRebuild();
        MarkUnwanted(Entry(datasets, SingleC64, 5));

        DatLineStore.ConstructionCount = 0;
        Assert.NotNull(Targeted(datasets, SingleC64));
        Assert.Equal(1, DatLineStore.ConstructionCount);

        DatLineStore.ConstructionCount = 0;
        FullRebuild();
        Assert.Equal(AllLeafIds.Length, DatLineStore.ConstructionCount);
    }

    [Fact]
    public void TargetedRefresh_KeepsEveryOtherDatasetInstanceAndTheListOrder()
    {
        var datasets = FullRebuild();
        var before   = datasets.ToList();
        var index    = datasets.IndexOf(DatasetOf(datasets, GroupLeafB));

        MarkUnwanted(Entry(datasets, GroupLeafB, 3));
        var refresh = Targeted(datasets, GroupLeafB);

        Assert.NotNull(refresh);
        Assert.Equal(before.Count, datasets.Count);
        for (int i = 0; i < datasets.Count; i++)
        {
            if (i == index)
            {
                Assert.Same(before[i], refresh!.Previous);
                Assert.Same(datasets[i], refresh.Current);
                Assert.NotSame(before[i], datasets[i]);
            }
            else
            {
                Assert.Same(before[i], datasets[i]);                  // not reloaded, not even re-wrapped
                Assert.Same(before[i].Entries, datasets[i].Entries);
            }
        }
    }

    [Fact]
    public void TargetedRefresh_DoesNotReadUnrelatedLeafDatabases()
    {
        // Change ANOTHER leaf behind the Library's back: a targeted refresh of psx must not pick it up (it never
        // opens that database), while a full rebuild would. This is the "no accidental reload" guarantee.
        var datasets = FullRebuild();
        var other    = Entry(datasets, SingleC64, 4);
        new DatLineStore(other.DbPath).UpdateReleaseStatus(other.ReleaseId, "lost");

        MarkUnwanted(Entry(datasets, SinglePsx, 5));
        Assert.NotNull(Targeted(datasets, SinglePsx));

        Assert.Equal("Missing", DatasetOf(datasets, SingleC64).Entries.Single(e => e.ReleaseId == other.ReleaseId).Status);
        Assert.Equal("Lost",    DatasetOf(FullRebuild(), SingleC64).Entries.Single(e => e.ReleaseId == other.ReleaseId).Status);
    }

    // ── Selection, filters, ordering (the MainWindow view state, with its pure parts) ──

    [Fact]
    public void SelectedRelease_StaysSelected_OnTheFreshEntry_WhenTheFilterStillShowsIt()
    {
        var datasets  = FullRebuild();
        IReadOnlyList<LibraryEntry> active = DatasetOf(datasets, SinglePsx).Entries;
        var selection = new LibrarySelectionState();
        var selected  = active.First(e => e.Status == "Missing");
        selection.Select(selected);

        MarkUnwanted(selected);
        var refresh = Targeted(datasets, SinglePsx)!;
        if (ReferenceEquals(active, refresh.Previous.Entries)) active = refresh.Current.Entries;   // as MainWindow does

        var resolved = selection.ResolveAfterFilter(LibraryFilterService.Apply(active, "", "All Statuses"));

        Assert.NotNull(resolved);
        Assert.Equal(selected.ReleaseId, resolved!.ReleaseId);
        Assert.NotSame(selected, resolved);                 // the fresh entry, not the stale object
        Assert.Equal("Unwanted", resolved.Status);          // so the detail pane shows the new state
        Assert.Equal(selected.ReleaseId, selection.SelectedReleaseId);
    }

    [Fact]
    public void SelectedRelease_IsCleared_WhenTheMutationRemovesItFromTheActiveFilter()
    {
        var datasets  = FullRebuild();
        IReadOnlyList<LibraryEntry> active = DatasetOf(datasets, SinglePsx).Entries;
        var selection = new LibrarySelectionState();
        var selected  = active.First(e => e.Status == "Missing");
        selection.Select(selected);

        MarkUnwanted(selected);
        var refresh = Targeted(datasets, SinglePsx)!;
        if (ReferenceEquals(active, refresh.Previous.Entries)) active = refresh.Current.Entries;

        var filtered = LibraryFilterService.Apply(active, "", "Missing");

        Assert.DoesNotContain(filtered, e => e.ReleaseId == selected.ReleaseId);
        Assert.Null(selection.ResolveAfterFilter(filtered));   // never a stale highlight
        Assert.Null(selection.SelectedReleaseId);
    }

    [Fact]
    public void SelectionOfAnotherRelease_SurvivesTheRefresh()
    {
        var datasets  = FullRebuild();
        IReadOnlyList<LibraryEntry> active = DatasetOf(datasets, GroupLeafA).Entries;
        var selection = new LibrarySelectionState();
        selection.Select(active[0]);
        var keepId = active[0].ReleaseId;

        MarkUnwanted(active[5]);
        var refresh = Targeted(datasets, GroupLeafA)!;
        if (ReferenceEquals(active, refresh.Previous.Entries)) active = refresh.Current.Entries;

        Assert.Equal(keepId, selection.ResolveAfterFilter(LibraryFilterService.Apply(active, "", "All Statuses"))?.ReleaseId);
    }

    [Theory]
    [InlineData("",        "All Statuses")]
    [InlineData("",        "Missing")]
    [InlineData("",        "Unwanted")]
    [InlineData("",        "Present")]
    [InlineData("",        "Pending")]
    [InlineData("",        "Hidden")]
    [InlineData("",        "New")]
    [InlineData("game 0",  "All Statuses")]
    [InlineData("GAME 1",  "Missing")]
    [InlineData("scraped", "All Statuses")]
    [InlineData("no-such", "All Statuses")]
    public void ActiveFilterAndOrdering_MatchTheFullRebuild(string search, string status)
    {
        var datasets = FullRebuild();
        MarkUnwanted(DatasetOf(datasets, SinglePsx).Entries.First(e => e.Status == "Missing"));
        RestoreWanted(DatasetOf(datasets, SinglePsx).Entries.First(e => e.Status == "Unwanted"));
        Assert.NotNull(Targeted(datasets, SinglePsx));

        var reference = LibraryFilterService.Apply(DatasetOf(FullRebuild(), SinglePsx).Entries, search, status);
        var targeted  = LibraryFilterService.Apply(DatasetOf(datasets,      SinglePsx).Entries, search, status);

        // Same rows, same order, same visible state.
        Assert.Equal(reference.Select(e => $"{e.ReleaseId}|{e.DisplayName}|{e.Status}|{e.ShowInCatalog}"),
                     targeted .Select(e => $"{e.ReleaseId}|{e.DisplayName}|{e.Status}|{e.ShowInCatalog}"));
    }

    [Fact]
    public void EntryOrder_IsUnchangedByAStatusMutation()
    {
        var datasets = FullRebuild();
        var before   = DatasetOf(datasets, SinglePsx).Entries.Select(e => e.ReleaseId).ToList();

        MarkUnwanted(Entry(datasets, SinglePsx, 7));
        Assert.NotNull(Targeted(datasets, SinglePsx));

        Assert.Equal(before, DatasetOf(datasets, SinglePsx).Entries.Select(e => e.ReleaseId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlatformAndDatLineSelectors_AreIdenticalToTheFullRebuild(bool markGroupLeaves)
    {
        // MainWindow does not repopulate the selectors after a targeted refresh — so what they would be rebuilt
        // from must be unchanged: same platforms, same DAT-line items, same order (Library and Catalog flavours).
        var datasets = FullRebuild();
        MarkUnwanted(Entry(datasets, GroupLeafC, 4));
        Assert.NotNull(Targeted(datasets, GroupLeafC));
        var reference = FullRebuild();

        Assert.Equal(reference.Select(d => d.Platform).Distinct(), datasets.Select(d => d.Platform).Distinct());
        foreach (var platform in reference.Select(d => d.Platform).Distinct())
            Assert.Equal(LibrarySelectorItemBuilder.Build(reference, platform, GroupLeafIds, markGroupLeaves),
                         LibrarySelectorItemBuilder.Build(datasets,  platform, GroupLeafIds, markGroupLeaves));
    }

    // ── Visible Library vs inactive Library ───────────────────────────────────

    [Fact]
    public void LeafNotOnScreen_IsRefreshed_WhileTheDisplayedLeafIsLeftAlone()
    {
        var datasets = FullRebuild();
        IReadOnlyList<LibraryEntry> active = DatasetOf(datasets, SingleC64).Entries;   // the view shows another leaf
        var shown = active;

        MarkUnwanted(Entry(datasets, SinglePsx, 6));
        var refresh = Targeted(datasets, SinglePsx)!;
        if (ReferenceEquals(active, refresh.Previous.Entries)) active = refresh.Current.Entries;

        Assert.Same(shown, active);                                         // displayed entries not swapped
        Assert.Same(shown, DatasetOf(datasets, SingleC64).Entries);
        AssertEqualsFullRebuild(datasets);                                  // …and the hidden leaf is current
    }

    [Fact]
    public void LibraryNeverBuilt_TheDeferredFirstBuildSeesEveryMutation()
    {
        // Library not opened yet: MainWindow only marks it dirty and the first open runs the full build. That build
        // must equal an already-built Library kept current with targeted refreshes.
        var built = FullRebuild();

        MarkUnwanted(Entry(built, SinglePsx, 8));
        Assert.NotNull(Targeted(built, SinglePsx));
        RestoreWanted(DatasetOf(built, GroupLeafB).Entries.First(e => e.Status == "Unwanted"));
        Assert.NotNull(Targeted(built, GroupLeafB));

        Assert.Equal(Snapshot(FullRebuild()), Snapshot(built));
    }

    // ── Sequences ─────────────────────────────────────────────────────────────

    [Fact]
    public void ManySequentialChanges_AcrossLeaves_StayEqualToTheFullRebuild_OneLeafOpenEach()
    {
        var datasets = FullRebuild();
        var steps = new (string Leaf, int Index, bool Unwanted)[]
        {
            (SinglePsx,  5, true),  (SinglePsx,  6, true),  (GroupLeafA, 4, true),  (SinglePsx,  5, false),
            (GroupLeafB, 5, true),  (SingleC64,  7, true),  (GroupLeafA, 4, false), (SinglePsx,  2, false),
            (GroupLeafC, 6, true),  (SingleC64,  7, false), (SinglePsx,  6, false), (GroupLeafB, 5, false),
        };

        foreach (var (leaf, index, unwanted) in steps)
        {
            var entry = Entry(datasets, leaf, index);     // always the CURRENT entry object, as the detail pane holds
            if (unwanted) MarkUnwanted(entry); else RestoreWanted(entry);

            DatLineStore.ConstructionCount = 0;
            Assert.NotNull(Targeted(datasets, leaf));
            Assert.Equal(1, DatLineStore.ConstructionCount);

            AssertEqualsFullRebuild(datasets);
        }
    }

    [Fact]
    public void RepeatedRefreshWithoutAMutation_IsIdempotent()
    {
        var datasets = FullRebuild();
        var before   = Snapshot(datasets);

        Assert.NotNull(Targeted(datasets, GroupLeafA));
        Assert.NotNull(Targeted(datasets, GroupLeafA));

        Assert.Equal(before, Snapshot(datasets));
    }

    // ── No stale status / counts / metadata / tier ────────────────────────────

    [Fact]
    public void RefreshedLeaf_CarriesNoStaleMetadataOrTier()
    {
        var datasets = FullRebuild();
        var entries  = DatasetOf(datasets, SinglePsx).Entries;
        var present  = entries.First(e => e.Status == "Present");
        var store    = new DatLineStore(present.DbPath);

        // Things a purge / a scrape change in the SAME leaf, alongside the status flip.
        store.DeleteReleaseContentLinks(present.ReleaseId);                // tier came from this link
        store.SaveReleaseMetadata(new ReleaseMetadataRecord { ReleaseId = entries[6].ReleaseId, Title = "Fresh Title" });
        MarkUnwanted(entries[5]);

        Assert.NotNull(Targeted(datasets, SinglePsx));
        var after = DatasetOf(datasets, SinglePsx).Entries;

        Assert.Equal("",            after.Single(e => e.ReleaseId == present.ReleaseId).Tier);
        Assert.Equal("Fresh Title", after.Single(e => e.ReleaseId == entries[6].ReleaseId).DisplayName);
        Assert.Equal("Unwanted",    after.Single(e => e.ReleaseId == entries[5].ReleaseId).Status);
        AssertEqualsFullRebuild(datasets);
    }

    [Fact]
    public void StatusCounts_DerivedFromTheRefreshedLeaf_MatchTheDatabase()
    {
        var datasets = FullRebuild();
        MarkUnwanted(DatasetOf(datasets, GroupLeafA).Entries.First(e => e.Status == "Missing"));
        MarkUnwanted(DatasetOf(datasets, GroupLeafA).Entries.First(e => e.Status == "Pending"));
        Assert.NotNull(Targeted(datasets, GroupLeafA));

        var entries = DatasetOf(datasets, GroupLeafA).Entries;
        var db      = Store(GroupLeafA).GetAllStatusCounts();

        Assert.Equal(db.Unwanted, entries.Count(e => e.Status == "Unwanted"));
        Assert.Equal(db.Present,  entries.Count(e => e.Status == "Present"));
        Assert.Equal(db.Missing,  entries.Count(e => e.Status == "Missing"));
        Assert.Equal(db.Pending,  entries.Count(e => e.Status == "Pending"));
        Assert.Equal(3, db.Unwanted);   // 1 seeded + 2 marked
    }

    [Fact]
    public void FlagImages_ResolvedBeforeTheRefresh_AreKeptForUnchangedLanguages()
    {
        var datasets = FullRebuild();
        var entries  = DatasetOf(datasets, SinglePsx).Entries;
        IReadOnlyList<Avalonia.Media.Imaging.Bitmap> flags = new Avalonia.Media.Imaging.Bitmap[1];   // stands for a resolved list
        entries[5].FlagImages = flags;

        MarkUnwanted(entries[5]);
        Assert.NotNull(Targeted(datasets, SinglePsx));

        var after = DatasetOf(datasets, SinglePsx).Entries;
        Assert.Same(flags, after.Single(e => e.ReleaseId == entries[5].ReleaseId).FlagImages);
        Assert.Empty(after.Single(e => e.ReleaseId == entries[6].ReleaseId).FlagImages);   // never resolved → still empty
    }

    // ── Fallback to the full rebuild (TryRefreshLeaf returns null and changes nothing) ──

    [Fact]
    public void Fallback_UnknownDatLine()
    {
        var datasets = FullRebuild();
        var before   = datasets.ToList();

        Assert.Null(Targeted(datasets, "no-such-dat-line"));
        Assert.Equal(before, datasets);
    }

    [Fact]
    public void Fallback_DatLineIdIsMatchedExactly_NotCaseInsensitively()
    {
        var datasets = FullRebuild();
        Assert.Null(Targeted(datasets, SinglePsx.ToUpperInvariant()));
    }

    [Fact]
    public void Fallback_LeafHadNoDatasetBefore()
    {
        var datasets = FullRebuild();
        datasets.Remove(DatasetOf(datasets, SingleC64));     // as if it had been empty at the last build
        var before = datasets.ToList();

        Assert.Null(Targeted(datasets, SingleC64));
        Assert.Equal(before, datasets);
    }

    [Fact]
    public void Fallback_LeafDatabaseMissing()
    {
        var datasets = FullRebuild();
        var before   = datasets.ToList();
        var path     = DbPath(SingleC64);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Move(path, path + ".away");

        Assert.Null(Targeted(datasets, SingleC64));
        Assert.Equal(before, datasets);
    }

    [Fact]
    public void Fallback_LeafNoLongerCatalogEnabled()
    {
        var datasets = FullRebuild();
        var lines    = _catalog.LoadDatLines();
        lines.Single(d => d.Id == SingleC64).CatalogEnabled = false;

        var result = LibraryDatasetBuilder.TryRefreshLeaf(datasets, _catalog.LoadPlatforms(), lines, SingleC64, _dataDir, Label);

        Assert.Null(result);
        Assert.NotNull(LibraryDatasetSelector.ByDatLineId(datasets, SingleC64));   // untouched; the full rebuild decides
    }

    [Fact]
    public void Fallback_LabelOrPlatformChanged_SelectorsWouldNeedRepopulating()
    {
        var datasets = FullRebuild();
        var before   = datasets.ToList();

        Assert.Null(LibraryDatasetBuilder.TryRefreshLeaf(
            datasets, _catalog.LoadPlatforms(), _catalog.LoadDatLines(), SinglePsx, _dataDir, dl => Label(dl) + " (renamed)"));

        var renamed = _catalog.LoadPlatforms();
        renamed.Single(p => p.Id == "psx").Name = "PlayStation 1";
        Assert.Null(LibraryDatasetBuilder.TryRefreshLeaf(datasets, renamed, _catalog.LoadDatLines(), SinglePsx, _dataDir, Label));

        Assert.Equal(before, datasets);
    }

    // ── Analytics: invalidate while hidden, recollect on open ─────────────────

    /// <summary>The release-status roll-up BuildAnalytics collects: one GetAllStatusCounts per DAT line.</summary>
    private (int Missing, int Pending, int Outdated, int Present, int Lost, int Unwanted) CollectStatusCounts()
    {
        int m = 0, pe = 0, o = 0, pr = 0, l = 0, u = 0;
        foreach (var dl in _catalog.LoadDatLines())
        {
            var c = new DatLineStore(Path.Combine(_dataDir, dl.DataStorePath)).GetAllStatusCounts();
            m += c.Missing; pe += c.Pending; o += c.Outdated; pr += c.Present; l += c.Lost; u += c.Unwanted;
        }
        return (m, pe, o, pr, l, u);
    }

    [Fact]
    public void Analytics_Hidden_MutationOnlyMarksStale_AndOpensNoLeafDatabase()
    {
        var freshness = new AnalyticsFreshness();
        var datasets  = FullRebuild();
        MarkUnwanted(Entry(datasets, SinglePsx, 5));

        DatLineStore.ConstructionCount = 0;
        var rebuildNow = freshness.Invalidate(viewVisible: false);

        Assert.False(rebuildNow);
        Assert.True(freshness.IsStale);
        Assert.Equal(0, DatLineStore.ConstructionCount);   // the all-DAT-lines pass did not run
    }

    [Fact]
    public void Analytics_OpenedAfterAMutation_ShowsTheMutatedCounts_SameAsAnEagerRebuild()
    {
        var freshness = new AnalyticsFreshness();
        int builds    = 0;
        (int Missing, int Pending, int Outdated, int Present, int Lost, int Unwanted) shown = default;
        void Build() { builds++; shown = CollectStatusCounts(); freshness.MarkBuilt(); }   // = BuildAnalytics

        Build();                                            // Analytics opened once
        var before   = shown;
        var datasets = FullRebuild();

        MarkUnwanted(DatasetOf(datasets, SinglePsx).Entries.First(e => e.Status == "Missing"));
        if (freshness.Invalidate(viewVisible: false)) Build();     // the handler, with Analytics hidden
        var eager = CollectStatusCounts();                         // what the old eager rebuild would have collected

        Assert.Equal(1, builds);                            // nothing recollected while hidden
        Assert.Equal(before, shown);                        // …so the held data is behind, and flagged as such
        Assert.True(freshness.IsStale);

        Build();                                            // SetActive(NavAnalytics)

        Assert.Equal(2, builds);
        Assert.False(freshness.IsStale);
        Assert.Equal(eager, shown);
        Assert.Equal(before.Unwanted + 1, shown.Unwanted);
        Assert.Equal(before.Missing  - 1, shown.Missing);
    }

    [Fact]
    public void Analytics_Visible_MutationRecollectsImmediately_ExactlyOnce()
    {
        var freshness = new AnalyticsFreshness();
        int builds    = 0;
        void Build() { builds++; freshness.MarkBuilt(); }

        if (freshness.Invalidate(viewVisible: true)) Build();

        Assert.Equal(1, builds);
        Assert.False(freshness.IsStale);
    }

    [Fact]
    public void Analytics_ManyHiddenMutations_CostOneRecollectionOnOpen_NotOneEach()
    {
        var freshness = new AnalyticsFreshness();
        int builds    = 0;
        void Build() { builds++; freshness.MarkBuilt(); }

        for (int i = 0; i < 25; i++)
            if (freshness.Invalidate(viewVisible: false)) Build();
        Assert.Equal(0, builds);
        Assert.True(freshness.IsStale);

        Build();                                            // the open
        Assert.Equal(1, builds);
        Assert.False(freshness.IsStale);
    }

    [Fact]
    public void Analytics_StartsFresh_AndABuildClearsStaleness()
    {
        var freshness = new AnalyticsFreshness();
        Assert.False(freshness.IsStale);

        freshness.Invalidate(viewVisible: false);
        freshness.MarkBuilt();

        Assert.False(freshness.IsStale);
    }

    // ── MainWindow wiring (source-level, like the other handler-order tests) ──

    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arkadia.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, rel);
    }

    private static string Main => File.ReadAllText(RepoFile("MainWindow.axaml.cs")).Replace("\r\n", "\n");

    private static string Region(string src, string start, string end)
    {
        var a = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"start marker not found: {start}");
        var b = src.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"end marker not found: {end}");
        return src.Substring(a, b - a);
    }

    private static void AssertInOrder(string region, params string[] parts)
    {
        int at = 0;
        foreach (var part in parts)
        {
            var i = region.IndexOf(part, at, StringComparison.Ordinal);
            Assert.True(i >= 0, $"missing or out of order: {part}");
            at = i + part.Length;
        }
    }

    [Fact]
    public void MarkUnwanted_RestoreWanted_Purge_UseTheTargetedRefresh_AndStillConvergeSystems()
    {
        var m = Main;
        var regions = new[]
        {
            Region(m, "var restoreBtn = MakeActionButton(\"Restore Wanted\"", "DetailActionsPanel.Children.Add(restoreBtn);"),
            Region(m, "var unwantedBtn = MakeActionButton(\"Mark as Unwanted\"", "DetailActionsPanel.Children.Add(unwantedBtn);"),
            Region(m, "await new InfoDialog(\"Purge Complete\"", "private static Button MakeActionButton("),
        };

        foreach (var r in regions)
        {
            Assert.DoesNotContain("RebuildLibraryDatasets();", r);   // no all-DAT-lines Library rebuild
            Assert.DoesNotContain("BuildAnalytics();", r);           // no all-DAT-lines Analytics pass
            AssertInOrder(r,
                "RefreshLibraryLeafAfterReleaseMutation(entry.DatLineId);",
                "InvalidateAnalyticsAfterReleaseMutation();",
                "ConvergeSystemsAfterLeafReleaseMutation(entry.DatLineId, entry.HardwareFamilyId);");   // Systems unchanged
        }

        // The mutations themselves are untouched.
        Assert.Contains("store.RestoreWantedRelease(entry.ReleaseId);", regions[0]);
        AssertInOrder(regions[1], "store.UpdateReleaseStatus(entry.ReleaseId, \"unwanted\");", "store.SetShowInCatalog(entry.ReleaseId, false);");
        Assert.Contains("RefreshDiskDetailIfSelected();", regions[2]);
    }

    [Fact]
    public void TargetedRefresh_DefersWhenTheLibraryWasNeverBuilt_AndFallsBackToTheFullRebuild()
    {
        var r = Region(Main, "private void RefreshLibraryLeafAfterReleaseMutation(string datLineId)", "private void BuildLibraryDatasetsCore()");

        AssertInOrder(r, "if (!_libraryBuilt)", "_libraryDirty = true;", "return;");                       // inactive Library: no leaf opened
        AssertInOrder(r, "Library.LibraryDatasetBuilder.TryRefreshLeaf(", "if (refresh is null)", "BuildLibraryDatasetsCore();");
        AssertInOrder(r, "if (ReferenceEquals(_activeDatasetEntries, refresh.Previous.Entries))",
                         "_activeDatasetEntries = refresh.Current.Entries;", "ApplyLibraryFilter();");
        Assert.DoesNotContain("_librarySelection.Clear()", r);                                             // selection kept (by ReleaseId)
    }

    [Fact]
    public void FullRebuild_AndTargetedRefresh_ShareOneLeafBuilder()
    {
        var core = Region(Main, "private void BuildLibraryDatasetsCore()", "private static string Capitalize(");
        Assert.Contains("Library.LibraryDatasetBuilder.BuildAll(allPlatforms, allDatLines, _dataDir, DatLineLabel);", core);
        Assert.DoesNotContain("new DatLineStore(", core);            // the per-leaf load lives in the builder only

        var builder = File.ReadAllText(RepoFile("Library/LibraryDatasetBuilder.cs"));
        Assert.Equal(2, builder.Split("BuildLeaf(dl, ResolvePlatformName(platforms, dl), dataDir, datLineLabel)").Length - 1);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(builder, @"new DatLineStore\("));
    }

    [Fact]
    public void Analytics_IsRecollectedOnEveryOpen_AndInvalidatedOnlyWhenHidden()
    {
        var m = Main;

        // Opening Analytics still recollects unconditionally — freshness on open is unchanged.
        AssertInOrder(Region(m, "private void SetActive(Button btn)", "// ── Analytics ──"), "if (btn == NavAnalytics)", "BuildAnalytics();");

        var invalidate = Region(m, "private void InvalidateAnalyticsAfterReleaseMutation()", "private void BuildAnalytics()");
        AssertInOrder(invalidate, "_analyticsFreshness.Invalidate(viewVisible: _activeButton == NavAnalytics)", "BuildAnalytics();");

        // Every build clears staleness; reports never use data a mutation left behind.
        AssertInOrder(Region(m, "private void BuildAnalytics()", "// ── Collect catalog-level data"), "_analyticsFreshness.MarkBuilt();");
        AssertInOrder(Region(m, "private async void OnAnalyticsGenerateReports(", "var reportsDir"),
                      "if (_analyticsData is not null && _analyticsFreshness.IsStale)", "BuildAnalytics();", "if (_analyticsData is null)");
    }

    [Fact]
    public void OtherLibraryMutations_StillUseTheFullRebuild()
    {
        // Out of scope: anything that is not a single release's own mutation keeps the historical rebuild (bulk
        // operations, imports, ingestion, volume operations, deletes). The targeted refresh has exactly four callers:
        // Mark as Unwanted, Restore Wanted, Purge release, Hide/Show in Catalog.
        var m    = Main;
        var bulk = Region(m, "private async void OnLibraryBulkOperations(", "private void OnLibrarySelectionChanged(");
        AssertInOrder(bulk, "RebuildLibraryDatasets();", "BuildAnalytics();", "ApplyLibraryFilter();");
        Assert.DoesNotContain("RefreshLibraryLeafAfterReleaseMutation", bulk);
        Assert.Equal(4, m.Split("RefreshLibraryLeafAfterReleaseMutation(entry.DatLineId);").Length - 1);
    }

    // ── PERFORMANCE-01B: Hide / Show in Catalog ───────────────────────────────

    // The exact statement of the Hide/Show handler: flip the release's own flag, nothing else.
    private static void ToggleCatalogVisibility(LibraryEntry e)
        => new DatLineStore(e.DbPath).SetShowInCatalog(e.ReleaseId, !e.ShowInCatalog);

    [Theory]
    [InlineData(SinglePsx)]
    [InlineData(SingleC64)]
    [InlineData(GroupLeafA)]
    [InlineData(GroupLeafC)]
    public void HideThenShow_MatchesFullRebuildAtEachStep_AndNeverTouchesTheStatus(string leafId)
    {
        var datasets = FullRebuild();
        var target   = DatasetOf(datasets, leafId).Entries.First(e => e.Status == "Missing" && e.ShowInCatalog);

        ToggleCatalogVisibility(target);                                   // Hide from Catalog
        Assert.NotNull(Targeted(datasets, leafId));
        var hidden = DatasetOf(datasets, leafId).Entries.Single(e => e.ReleaseId == target.ReleaseId);
        Assert.False(hidden.ShowInCatalog);
        Assert.Equal("Missing", hidden.Status);                            // visibility is not a status
        AssertEqualsFullRebuild(datasets);

        ToggleCatalogVisibility(hidden);                                   // Show in Catalog — from the FRESH entry
        Assert.NotNull(Targeted(datasets, leafId));
        var shown = DatasetOf(datasets, leafId).Entries.Single(e => e.ReleaseId == target.ReleaseId);
        Assert.True(shown.ShowInCatalog);
        Assert.Equal("Missing", shown.Status);
        AssertEqualsFullRebuild(datasets);
    }

    [Theory]
    [InlineData("Present")]
    [InlineData("Pending")]
    [InlineData("Unwanted")]
    public void HideShow_WorksForEveryStatus_AndLeavesItUnchanged(string status)
    {
        // The button exists for all statuses. Showing an Unwanted release makes it visible but keeps the veto.
        var datasets = FullRebuild();
        var target   = DatasetOf(datasets, SinglePsx).Entries.First(e => e.Status == status);

        ToggleCatalogVisibility(target);
        Assert.NotNull(Targeted(datasets, SinglePsx));

        var after = DatasetOf(datasets, SinglePsx).Entries.Single(e => e.ReleaseId == target.ReleaseId);
        Assert.Equal(!target.ShowInCatalog, after.ShowInCatalog);
        Assert.Equal(status, after.Status);
        Assert.Equal(target.Tier, after.Tier);
        AssertEqualsFullRebuild(datasets);
    }

    [Fact]
    public void HideShow_TargetedRefresh_OpensExactlyOneLeafDatabase_AndKeepsEveryOtherDataset()
    {
        var datasets = FullRebuild();
        var before   = datasets.ToList();
        var index    = datasets.IndexOf(DatasetOf(datasets, GroupLeafB));
        ToggleCatalogVisibility(Entry(datasets, GroupLeafB, 4));

        DatLineStore.ConstructionCount = 0;
        Assert.NotNull(Targeted(datasets, GroupLeafB));
        Assert.Equal(1, DatLineStore.ConstructionCount);                   // a full rebuild opens all five

        for (int i = 0; i < datasets.Count; i++)
            if (i != index) Assert.Same(before[i], datasets[i]);           // unrelated leaves: not reloaded
        Assert.NotSame(before[index], datasets[index]);
    }

    [Fact]
    public void HideShow_DoesNotReadUnrelatedLeafDatabases()
    {
        // A change made behind the Library's back in ANOTHER leaf is not picked up: that database is never opened.
        var datasets = FullRebuild();
        var other    = Entry(datasets, SingleC64, 4);
        new DatLineStore(other.DbPath).SetShowInCatalog(other.ReleaseId, false);

        ToggleCatalogVisibility(Entry(datasets, SinglePsx, 5));
        Assert.NotNull(Targeted(datasets, SinglePsx));

        Assert.True(DatasetOf(datasets,      SingleC64).Entries.Single(e => e.ReleaseId == other.ReleaseId).ShowInCatalog);
        Assert.False(DatasetOf(FullRebuild(), SingleC64).Entries.Single(e => e.ReleaseId == other.ReleaseId).ShowInCatalog);
    }

    [Theory]
    [InlineData("",       "All Statuses")]
    [InlineData("",       "Hidden")]
    [InlineData("",       "Missing")]
    [InlineData("",       "Unwanted")]
    [InlineData("game 0", "Hidden")]
    [InlineData("GAME 1", "All Statuses")]
    public void HideShow_ActiveFilterAndOrdering_MatchTheFullRebuild(string search, string status)
    {
        var datasets = FullRebuild();
        ToggleCatalogVisibility(DatasetOf(datasets, SinglePsx).Entries.First(e => e.Status == "Missing"));   // hide one
        ToggleCatalogVisibility(DatasetOf(datasets, SinglePsx).Entries.First(e => !e.ShowInCatalog));        // show one
        Assert.NotNull(Targeted(datasets, SinglePsx));

        var reference = LibraryFilterService.Apply(DatasetOf(FullRebuild(), SinglePsx).Entries, search, status);
        var targeted  = LibraryFilterService.Apply(DatasetOf(datasets,      SinglePsx).Entries, search, status);

        Assert.Equal(reference.Select(e => $"{e.ReleaseId}|{e.DisplayName}|{e.Status}|{e.ShowInCatalog}"),
                     targeted .Select(e => $"{e.ReleaseId}|{e.DisplayName}|{e.Status}|{e.ShowInCatalog}"));
    }

    [Fact]
    public void HideShow_SelectedRelease_StaysSelectedOnTheFreshEntry_OrClearsWhenFilteredOut()
    {
        var datasets  = FullRebuild();
        IReadOnlyList<LibraryEntry> active = DatasetOf(datasets, SinglePsx).Entries;
        var selection = new LibrarySelectionState();
        var selected  = active.First(e => e.Status == "Missing" && e.ShowInCatalog);
        selection.Select(selected);

        // Hide under "All Statuses": still listed → still selected, on the fresh entry (the button now reads "Show").
        ToggleCatalogVisibility(selected);
        var refresh = Targeted(datasets, SinglePsx)!;
        if (ReferenceEquals(active, refresh.Previous.Entries)) active = refresh.Current.Entries;   // as MainWindow does
        var resolved = selection.ResolveAfterFilter(LibraryFilterService.Apply(active, "", "All Statuses"));
        Assert.NotNull(resolved);
        Assert.NotSame(selected, resolved);
        Assert.Equal(selected.ReleaseId, resolved!.ReleaseId);
        Assert.False(resolved.ShowInCatalog);

        // Show again under the "Hidden" filter: the row leaves the list → selection cleared, never a stale highlight.
        ToggleCatalogVisibility(resolved);
        refresh = Targeted(datasets, SinglePsx)!;
        if (ReferenceEquals(active, refresh.Previous.Entries)) active = refresh.Current.Entries;
        var filtered = LibraryFilterService.Apply(active, "", "Hidden");
        Assert.DoesNotContain(filtered, e => e.ReleaseId == selected.ReleaseId);
        Assert.Null(selection.ResolveAfterFilter(filtered));
        Assert.Null(selection.SelectedReleaseId);
    }

    [Fact]
    public void HideShow_KeepsMetadataRomFilesAndResolvedFlags()
    {
        var datasets = FullRebuild();
        var target   = DatasetOf(datasets, SinglePsx).Entries.First(e => e.Metadata is not null);
        IReadOnlyList<Avalonia.Media.Imaging.Bitmap> flags = new Avalonia.Media.Imaging.Bitmap[1];   // stands for a resolved list
        target.FlagImages = flags;

        ToggleCatalogVisibility(target);
        Assert.NotNull(Targeted(datasets, SinglePsx));

        var after = DatasetOf(datasets, SinglePsx).Entries.Single(e => e.ReleaseId == target.ReleaseId);
        Assert.Equal(JsonSerializer.Serialize(target.Metadata), JsonSerializer.Serialize(after.Metadata));
        Assert.Equal(target.DisplayName, after.DisplayName);
        Assert.Equal(JsonSerializer.Serialize(target.RomFiles), JsonSerializer.Serialize(after.RomFiles));
        Assert.Same(flags, after.FlagImages);
        AssertEqualsFullRebuild(datasets);
    }

    [Fact]
    public void HideShow_MixedWithWantedUnwantedChanges_StaysEqualToTheFullRebuild()
    {
        var datasets = FullRebuild();
        var steps = new (string Leaf, int Index, char Op)[]     // H = toggle visibility, U = mark unwanted, W = restore wanted
        {
            (SinglePsx, 5, 'H'), (SinglePsx, 6, 'U'), (GroupLeafA, 4, 'H'), (SinglePsx, 6, 'H'), (SinglePsx, 5, 'H'),
            (GroupLeafA, 4, 'U'), (SinglePsx, 6, 'W'), (GroupLeafA, 4, 'H'), (SingleC64, 7, 'H'), (GroupLeafA, 4, 'W'),
        };

        foreach (var (leaf, index, op) in steps)
        {
            var entry = Entry(datasets, leaf, index);
            if (op == 'H') ToggleCatalogVisibility(entry); else if (op == 'U') MarkUnwanted(entry); else RestoreWanted(entry);

            DatLineStore.ConstructionCount = 0;
            Assert.NotNull(Targeted(datasets, leaf));
            Assert.Equal(1, DatLineStore.ConstructionCount);
            AssertEqualsFullRebuild(datasets);
        }
    }

    [Fact]
    public void HideShow_DoesNotChangeAnythingAnalyticsCollects()
    {
        // Why the handler needs no Analytics invalidation: the collected data is identical before and after.
        string Collected() => string.Join(";", AllLeafIds.Select(id =>
        {
            var s = Store(id);
            var a = s.GetAnalyticsSummary();
            return $"{id}:{s.GetAllStatusCounts()}:{a.TotalSourceBytes}:{a.TotalDerivedBytes}:{a.TotalDerivedCount}:" +
                   $"{JsonSerializer.Serialize(a.DerivedByStrategy)}:{JsonSerializer.Serialize(a.ExtensionCounts)}";
        }));
        var datasets = FullRebuild();
        var before   = Collected();

        ToggleCatalogVisibility(Entry(datasets, SinglePsx, 0));
        ToggleCatalogVisibility(Entry(datasets, GroupLeafB, 5));

        Assert.Equal(before, Collected());
    }

    [Fact]
    public void HideShow_Handler_UsesTheTargetedRefresh_WithItsFallback_AndTheSameMutation()
    {
        var m = Main;
        var r = Region(m, "private void AddCatalogVisibilityButton(", "private async System.Threading.Tasks.Task OnPurgeRelease(");

        // The mutation and the button are untouched.
        AssertInOrder(r,
            "var label = entry.ShowInCatalog ? \"Hide from Catalog\" : \"Show in Catalog\";",
            "if (entry.DbPath.Length > 0 && File.Exists(entry.DbPath))",
            "var store = new Data.DatLineStore(entry.DbPath);",
            "store.SetShowInCatalog(entry.ReleaseId, !entry.ShowInCatalog);",
            "RefreshLibraryLeafAfterReleaseMutation(entry.DatLineId);");
        Assert.DoesNotContain("UpdateReleaseStatus", r);
        Assert.DoesNotContain("RestoreWantedRelease", r);

        // No all-DAT-lines work, and — as before — no Analytics pass and no Systems refresh for a visibility flip.
        Assert.DoesNotContain("RebuildLibraryDatasets();", r);
        Assert.DoesNotContain("BuildAnalytics();", r);
        Assert.DoesNotContain("RefreshSystems", r);
        Assert.DoesNotContain("ConvergeSystemsAfterLeafReleaseMutation", r);

        // The shared refresh keeps its full-rebuild fallback.
        var refresh = Region(m, "private void RefreshLibraryLeafAfterReleaseMutation(string datLineId)", "private void BuildLibraryDatasetsCore()");
        AssertInOrder(refresh, "if (refresh is null)", "BuildLibraryDatasetsCore();", "ApplyLibraryFilter();");
    }
}
