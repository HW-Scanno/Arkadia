using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Arkadia;
using Arkadia.Data;
using Arkadia.Library;
using Arkadia.Providers;
using Xunit;

namespace Arkadia.Tests.Amp;

public sealed class AmpLocalPackageImportServiceTests : IDisposable
{
    private readonly string                  _baseDir;
    private readonly string                  _dataDir;
    private readonly CatalogService          _catalog;
    private readonly AmpExportWriterService  _writer;
    private readonly AmpPackageReaderService _reader;

    private const string HwFamilyId = "snes";
    private const string DatLineId  = "snes-nointro";
    private const string ReleaseId  = "rel-001";
    private const string DatName    = "Super Mario World (USA)";

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public AmpLocalPackageImportServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_baseDir, "data");
        Directory.CreateDirectory(_dataDir);
        _catalog = new CatalogService(_baseDir);
        _writer  = new AmpExportWriterService();
        _reader  = new AmpPackageReaderService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private LibraryEntry MakeEntry(string releaseId = ReleaseId)
    {
        var dbDir  = Path.Combine(_dataDir, "systems", HwFamilyId);
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, $"{DatLineId}.db");
        _ = new DatLineStore(dbPath); // ensure schema
        return new LibraryEntry
        {
            Name             = DatName,
            Platform         = "Console",
            HardwareFamilyId = HwFamilyId,
            DatLineId        = DatLineId,
            Status           = "Present",
            Region           = "wor",
            Languages        = "en",
            Format           = "rom",
            Size             = "512 KB",
            Tier             = "A",
            ReleaseId        = releaseId,
            DbPath           = dbPath,
        };
    }

    private DatLineStore OpenStore(LibraryEntry entry) => new(entry.DbPath);

    private AmpLocalPackageImportService MakeSvc() => new(_dataDir);

    private string PlaceMediaFile(string name, byte[]? content = null)
    {
        var path = Path.Combine(_baseDir, "media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? PngBytes);
        return path;
    }

    private static AmpExportPlanMediaEntry ExportEntry(
        string mediaType, string filePath,
        bool isPreferred = true, string credits = "Test Author") =>
        new(MediaType:   mediaType,
            FilePath:    filePath,
            Sha256:      ReleaseMediaCurationService.ComputeSha256(filePath)!,
            SizeBytes:   new FileInfo(filePath).Length,
            IsPreferred: isPreferred,
            Credits:     credits);

    private string CreateAmp(
        IReadOnlyList<AmpExportPlanMediaEntry>? media = null,
        string title = "Super Mario World")
    {
        var plan = new AmpExportPlan(
            HardwareFamilyId:     HwFamilyId,
            DatLineId:            DatLineId,
            SystemName:           "Super Nintendo",
            ReleaseCount:         1,
            ReleasesWithMetadata: 1,
            ReleasesWithMedia:    media?.Count > 0 ? 1 : 0,
            TotalMediaFiles:      media?.Count ?? 0,
            TotalBytes:           0L,
            ExclusionCount:       0,
            ExtraNotesCount:      0,
            Releases: [new AmpExportPlanRelease(
                ReleaseId:       ReleaseId,
                DatName:         DatName,
                Title:           title,
                OriginalTitle:   "",
                SortTitle:       "",
                Developer:       "Nintendo",
                Publisher:       "Nintendo",
                Year:            "1990",
                Languages:       "en",
                AlternateTitles: "",
                Description:     "Classic platformer",
                Genre:           "Platform",
                Subgenre:        "",
                Players:         "1-2",
                ReleaseType:     "retail",
                Rating:          "",
                HasMetadata:     true,
                MediaEntries:    media ?? [],
                ExclusionHashes: [],
                ExtraNotes:      null,
                Issues:          [])],
            Issues: []);
        var path = Path.Combine(_baseDir, Guid.NewGuid().ToString("N") + ".amp");
        _writer.Write(plan, path);
        return path;
    }

    private static AmpReleaseInfo MakeReleaseInfo(
        IReadOnlyList<AmpMediaEntryInfo>? media = null,
        string title = "Super Mario World") =>
        new(ReleaseId:       ReleaseId,
            DatName:         DatName,
            Title:           title,
            OriginalTitle:   "",
            SortTitle:       "",
            Developer:       "Nintendo",
            Publisher:       "Nintendo",
            Year:            "1990",
            Languages:       "en",
            AlternateTitles: "",
            Description:     "Classic platformer",
            Genre:           "Platform",
            Subgenre:        "",
            Players:         "1-2",
            ReleaseType:     "retail",
            Rating:          "",
            Media:           media ?? []);

    // ── 1. Proposals saved with "arkadia-media-pack" provider ────────────────

    [Fact]
    public async Task ImportAsync_MetadataOnly_CreatesProposalsWithArkadiaMediaPackProvider()
    {
        var amp     = CreateAmp();
        var entry   = MakeEntry();
        var release = MakeReleaseInfo();

        var summary = await MakeSvc().ImportAsync(entry, amp, release, [], extractMedia: false);

        Assert.True(summary.ProposalsSaved);
        var proposals = OpenStore(entry).LoadMetadataProposals(entry.ReleaseId, "arkadia-media-pack");
        Assert.NotEmpty(proposals);
        Assert.All(proposals, p => Assert.Equal("arkadia-media-pack", p.Provider));
    }

    // ── 2. No provider payload row written ───────────────────────────────────

    [Fact]
    public async Task ImportAsync_DoesNotWriteProviderPayloadRow()
    {
        var amp     = CreateAmp();
        var entry   = MakeEntry();
        var release = MakeReleaseInfo();

        await MakeSvc().ImportAsync(entry, amp, release, [], extractMedia: false);

        var payload = OpenStore(entry).LoadProviderPayload(entry.ReleaseId, "arkadia-media-pack");
        Assert.Null(payload);
    }

    // ── 3. autoApplyEmptyFields=false does not overwrite existing canonical ───

    [Fact]
    public async Task ImportAsync_AutoApplyEmptyFields_FalseByDefault_DoesNotOverrideExisting()
    {
        var amp     = CreateAmp(title: "Imported Title");
        var entry   = MakeEntry();
        var store   = OpenStore(entry);
        store.SaveReleaseMetadata(new ReleaseMetadataRecord
            { ReleaseId = ReleaseId, Title = "Existing Title" });

        await MakeSvc().ImportAsync(entry, amp, MakeReleaseInfo(title: "Imported Title"), [],
            autoApplyEmptyFields: false, extractMedia: false);

        var current = store.LoadReleaseMetadata()[ReleaseId];
        Assert.Equal("Existing Title", current.Title);
    }

    // ── 4. autoApplyEmptyFields=true fills empty canonical field ─────────────

    [Fact]
    public async Task ImportAsync_AutoApplyEmptyFields_True_FillsEmptyCanonical()
    {
        var amp     = CreateAmp(title: "Super Mario World");
        var entry   = MakeEntry();

        var summary = await MakeSvc().ImportAsync(
            entry, amp, MakeReleaseInfo(title: "Super Mario World"), [],
            autoApplyEmptyFields: true, extractMedia: false);

        Assert.True(summary.MetadataFieldsApplied > 0);
        var all = OpenStore(entry).LoadReleaseMetadata();
        Assert.True(all.TryGetValue(ReleaseId, out var current));
        Assert.Equal("Super Mario World", current.Title);
    }

    // ── 5. Extracted file SHA-256 matches AMP entry ───────────────────────────

    [Fact]
    public async Task ImportAsync_MediaExtracted_DestinationSha256MatchesAmpEntry()
    {
        var mediaFile   = PlaceMediaFile("cover.png");
        var exportEntry = ExportEntry("cover-front", mediaFile);
        var amp         = CreateAmp(media: [exportEntry]);

        _reader.TryReadReleases(amp, out var releases);
        var entry   = MakeEntry();

        var summary = await MakeSvc().ImportAsync(entry, amp, releases[0], []);

        Assert.Equal(1, summary.MediaFilesExtracted);
        var rows = OpenStore(entry).LoadMediaCurationRows(ReleaseId);
        Assert.Single(rows);
        Assert.Equal(exportEntry.Sha256, rows[0].FileSha256);
    }

    // ── 6. Excluded hash is skipped ───────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_LocalExcludedHash_SkipsMediaEntry()
    {
        var mediaFile   = PlaceMediaFile("cover.png");
        var exportEntry = ExportEntry("cover-front", mediaFile);
        var amp         = CreateAmp(media: [exportEntry]);

        _reader.TryReadReleases(amp, out var releases);
        var entry = MakeEntry();
        var store = OpenStore(entry);

        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "cover-front",
            FilePath:       Path.Combine(_dataDir, "dummy.png"),
            FileSha256:     exportEntry.Sha256,
            IsPreferred:    false,
            IsExcluded:     true,
            ExcludedReason: "User excluded",
            Credits:        null,
            Notes:          null));

        var summary = await MakeSvc().ImportAsync(entry, amp, releases[0], [],
            respectExcludedMedia: true);

        Assert.Equal(0, summary.MediaFilesExtracted);
        Assert.Equal(1, summary.MediaFilesSkippedExcluded);
    }

    // ── 7. Credits preserved on media curation row ────────────────────────────

    [Fact]
    public async Task ImportAsync_CreditsPreservedOnMediaRow()
    {
        var mediaFile   = PlaceMediaFile("cover.png");
        var exportEntry = ExportEntry("cover-front", mediaFile, credits: "Pixel Artist");
        var amp         = CreateAmp(media: [exportEntry]);

        _reader.TryReadReleases(amp, out var releases);
        var entry = MakeEntry();

        await MakeSvc().ImportAsync(entry, amp, releases[0], []);

        var rows = OpenStore(entry).LoadMediaCurationRows(ReleaseId);
        Assert.Single(rows);
        Assert.Equal("Pixel Artist", rows[0].Credits);
    }

    // ── 8. Preferred=true sets preferred when no existing preferred ───────────

    [Fact]
    public async Task ImportAsync_PreferredTrue_SetsPreferredWhenNoneExists()
    {
        var mediaFile   = PlaceMediaFile("cover.png");
        var exportEntry = ExportEntry("cover-front", mediaFile, isPreferred: true);
        var amp         = CreateAmp(media: [exportEntry]);

        _reader.TryReadReleases(amp, out var releases);
        var entry = MakeEntry();

        await MakeSvc().ImportAsync(entry, amp, releases[0], []);

        var rows = OpenStore(entry).LoadMediaCurationRows(ReleaseId);
        Assert.Single(rows);
        Assert.True(rows[0].IsPreferred);
    }

    // ── 9. Preferred=true does NOT override an existing preferred row ─────────

    [Fact]
    public async Task ImportAsync_PreferredTrue_DoesNotOverrideExistingPreferred()
    {
        var existingFile = PlaceMediaFile("existing.png");
        var newFile      = PlaceMediaFile("new.png",
            [0x89, 0x50, 0x4E, 0x47, 0xFF, 0xFE, 0xFD, 0xFC]);

        var exportEntry = ExportEntry("cover-front", newFile, isPreferred: true);
        var amp         = CreateAmp(media: [exportEntry]);

        _reader.TryReadReleases(amp, out var releases);
        var entry = MakeEntry();
        var store = OpenStore(entry);

        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "cover-front",
            FilePath:       existingFile,
            FileSha256:     ReleaseMediaCurationService.ComputeSha256(existingFile)!,
            IsPreferred:    true,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        await MakeSvc().ImportAsync(entry, amp, releases[0], []);

        var preferred = store.LoadMediaCurationRows(ReleaseId).Where(r => r.IsPreferred).ToList();
        Assert.Single(preferred);
        Assert.Equal(existingFile, preferred[0].FilePath);
    }

    // ── 10. SHA-256 mismatch counts as failed, no row inserted ───────────────

    [Fact]
    public async Task ImportAsync_Sha256Mismatch_SkipsFileAndCountsAsFailed()
    {
        var mediaFile   = PlaceMediaFile("cover.png");
        var exportEntry = ExportEntry("cover-front", mediaFile);
        var amp         = CreateAmp(media: [exportEntry]);

        _reader.TryReadReleases(amp, out var releases);
        var realMedia  = releases[0].Media[0];
        var badMedia   = realMedia with { Sha256 = new string('0', 64) };
        var badRelease = releases[0] with { Media = new[] { badMedia } };

        var entry   = MakeEntry();
        var summary = await MakeSvc().ImportAsync(entry, amp, badRelease, []);

        Assert.Equal(0, summary.MediaFilesExtracted);
        Assert.Equal(1, summary.MediaFilesFailedSha256);
        Assert.Empty(OpenStore(entry).LoadMediaCurationRows(ReleaseId));
    }

    // ── 11. skipExistingMedia skips already-present SHA-256 ──────────────────

    [Fact]
    public async Task ImportAsync_SkipExistingMedia_SkipsAlreadyPresentSha256()
    {
        var mediaFile   = PlaceMediaFile("cover.png");
        var exportEntry = ExportEntry("cover-front", mediaFile);
        var amp         = CreateAmp(media: [exportEntry]);

        _reader.TryReadReleases(amp, out var releases);
        var entry = MakeEntry();
        var store = OpenStore(entry);

        store.UpsertMediaCurationRow(new MediaCurationRow(
            ReleaseId:      ReleaseId,
            MediaType:      "cover-front",
            FilePath:       mediaFile,
            FileSha256:     exportEntry.Sha256,
            IsPreferred:    false,
            IsExcluded:     false,
            ExcludedReason: null,
            Credits:        null,
            Notes:          null));

        var summary = await MakeSvc().ImportAsync(entry, amp, releases[0], [],
            skipExistingMedia: true);

        Assert.Equal(0, summary.MediaFilesExtracted);
        Assert.Equal(1, summary.MediaFilesSkippedExisting);
    }

    // ── 12. Missing archive path counts as failed, does not throw ─────────────

    [Fact]
    public async Task ImportAsync_MissingArchivePath_SkipsEntryWithoutThrowing()
    {
        var amp   = CreateAmp(); // no media in archive
        var entry = MakeEntry();

        var badMedia = new AmpMediaEntryInfo(
            MediaType:   "cover-front",
            ArchivePath: "media/cover-front/rel-001/nonexistent.png",
            FileName:    "nonexistent.png",
            Sha256:      new string('a', 64),
            SizeBytes:   8L,
            Preferred:   false,
            Credits:     null);

        var summary = await MakeSvc().ImportAsync(
            entry, amp, MakeReleaseInfo(media: [badMedia]), []);

        Assert.Equal(0, summary.MediaFilesExtracted);
        Assert.Equal(1, summary.MediaFilesFailedSha256);
    }

    // ── 13. MatchKind is carried through to summary ───────────────────────────

    [Fact]
    public async Task ImportAsync_MatchKind_IncludedInSummary()
    {
        var amp   = CreateAmp();
        var entry = MakeEntry();

        var summary = await MakeSvc().ImportAsync(
            entry, amp, MakeReleaseInfo(), [],
            extractMedia: false,
            matchKind: AmpReleaseMatchKind.DatName);

        Assert.Equal(AmpReleaseMatchKind.DatName, summary.MatchKind);
    }

    // ── 14. extractMedia=false produces no curation rows ─────────────────────

    [Fact]
    public async Task ImportAsync_ExtractMediaFalse_DoesNotCreateCurationRows()
    {
        var mediaFile   = PlaceMediaFile("cover.png");
        var exportEntry = ExportEntry("cover-front", mediaFile);
        var amp         = CreateAmp(media: [exportEntry]);

        _reader.TryReadReleases(amp, out var releases);
        var entry = MakeEntry();

        var summary = await MakeSvc().ImportAsync(entry, amp, releases[0], [],
            extractMedia: false);

        Assert.Equal(0, summary.MediaFilesExtracted);
        Assert.Empty(OpenStore(entry).LoadMediaCurationRows(ReleaseId));
    }
}
