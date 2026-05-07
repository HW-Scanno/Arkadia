using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Arkadia.Data;
using Arkadia.Library;

namespace Arkadia.Providers;

public sealed record MediaDownloadSummary(
    int  Covers,
    int  Screenshots,
    int  Fanart,
    bool GotVideo,
    int  Logos,
    int  Marquees,
    int  Flyers,
    int  Manuals,
    int  PhysicalMedia,
    int  PhysicalTexture);

/// <summary>
/// Handles the non-UI portion of a manual ScreenScraper scrape: normalises and saves
/// provider proposals, saves the raw payload, writes the metadata JSON file, and
/// downloads all media assets.  Returns a summary of what was downloaded.
/// Rate-limit exceptions propagate to the caller; all other per-asset failures are swallowed.
/// </summary>
public sealed class ScreenScraperImportService(string dataDir)
{
    public async Task<MediaDownloadSummary> ImportAsync(
        LibraryEntry entry,
        ScreenScraperResult result,
        IReadOnlyList<MetadataValueMappingRecord> mappings,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // ── Build normalized proposals ──────────────────────────────────────
        var proposed = new Dictionary<string, string>(StringComparer.Ordinal);
        void Propose(string field, string value)
            { if (value.Length > 0) proposed[field] = value; }
        string Norm(string field, string value) =>
            MetadataValueNormalizer.Normalize(field, value, mappings);

        Propose("title",          result.Title);
        Propose("original_title", result.OriginalTitle);
        Propose("developer",      result.Developer);
        Propose("publisher",      result.Publisher);
        Propose("year",           result.Year);
        Propose("languages",      result.Languages);
        Propose("description",    result.Description);
        Propose("genre",          Norm("genre",        result.Genre));
        Propose("subgenre",       Norm("subgenre",     result.Subgenre));
        Propose("players",        Norm("players",      result.Players));
        Propose("rating",         Norm("rating",       result.Rating));

        var current = entry.Metadata ?? new ReleaseMetadataRecord { ReleaseId = entry.ReleaseId };
        var store   = new DatLineStore(entry.DbPath);

        progress?.Report("Saving provider proposals…");
        store.ApplyProviderProposals(entry.ReleaseId, ArkadiaProviders.ScreenScraper, proposed, current,
            autoApplyEmptyFields: false);

        // ── Save provider payload ───────────────────────────────────────────
        var payloadJson = result.RawJson.Length > 0 ? ScreenScraperPayloadSanitizer.SanitizeJson(result.RawJson) : "{}";
        store.SaveProviderPayload(entry.ReleaseId, ArkadiaProviders.ScreenScraper, payloadJson);

        var metaDir = Path.Combine(
            MediaStore.DatLinePath(dataDir, entry.HardwareFamilyId, entry.DatLineId),
            "metadata");
        Directory.CreateDirectory(metaDir);
        var metaFile = Path.Combine(metaDir,
            $"{MediaStore.ReleaseStem(entry.Name)}_screenscraper.json");
        await File.WriteAllTextAsync(metaFile, payloadJson, ct);

        // ── Download media ──────────────────────────────────────────────────
        MediaStore.EnsureMediaFolders(dataDir, entry.HardwareFamilyId, entry.DatLineId);

        var imgExts = ScreenScraperClient.ValidImageExts;
        var vidExts = ScreenScraperClient.ValidVideoExts;
        var docExts = ScreenScraperClient.ValidDocumentExts;

        string MediaStem(string sub) =>
            MediaStore.NextIndexedMediaStem(
                dataDir, entry.HardwareFamilyId, entry.DatLineId, entry.Name, sub);

        string CoverStem(string sub, string region) =>
            MediaStore.NextIndexedCoverStem(
                dataDir, entry.HardwareFamilyId, entry.DatLineId,
                entry.Name, sub, region.Length > 0 ? region : "wor");

        int coversCount   = 0, ssCount = 0, fanartCount = 0, logosCount = 0;
        int marqueesCount = 0, flyersCount = 0, manualsCount = 0;
        int physicalCount = 0, physicalTextureCount = 0;
        bool gotVideo = false;

        // Covers
        progress?.Report("Downloading covers…");
        foreach (var c in result.CoverFront)
            if (await TryDownload(c.Url, CoverStem("covers-front", c.Region), c.Format, imgExts, c.Size)) coversCount++;
        foreach (var c in result.CoverBack)
            if (await TryDownload(c.Url, CoverStem("covers-back",  c.Region), c.Format, imgExts, c.Size)) coversCount++;
        foreach (var c in result.CoverSpine)
            if (await TryDownload(c.Url, CoverStem("covers-spine", c.Region), c.Format, imgExts, c.Size)) coversCount++;
        foreach (var c in result.CoverWrap)
            if (await TryDownload(c.Url, CoverStem("covers-wrap",  c.Region), c.Format, imgExts, c.Size)) coversCount++;

        // Screenshots
        progress?.Report("Downloading screenshots…");
        foreach (var ss in result.TitleScreenshots)
            if (await TryDownload(ss.Url, MediaStem("screenshots-title"), ss.Format, imgExts, ss.Size)) ssCount++;
        foreach (var ss in result.GameplayScreenshots)
            if (await TryDownload(ss.Url, MediaStem("screenshots"),       ss.Format, imgExts, ss.Size)) ssCount++;

        // Fanart
        foreach (var f in result.Fanart)
            if (await TryDownload(f.Url, MediaStem("fanart"), f.Format, imgExts, f.Size)) fanartCount++;

        // Video
        if (result.Video is { } vid)
        {
            progress?.Report("Downloading video…");
            gotVideo = await TryDownload(vid.Url, MediaStem("videos"), vid.Format, vidExts, vid.Size);
        }

        // Logos
        foreach (var l in result.LogosHd)
            if (await TryDownload(l.Url, MediaStem("logos-hd"), l.Format, imgExts, l.Size)) logosCount++;
        foreach (var l in result.Logos)
            if (await TryDownload(l.Url, MediaStem("logos"),    l.Format, imgExts, l.Size)) logosCount++;

        // Marquees
        foreach (var m in result.Marquees)
            if (await TryDownload(m.Url, MediaStem("marquees"), m.Format, imgExts, m.Size)) marqueesCount++;

        // Flyers
        foreach (var f in result.Flyers)
            if (await TryDownload(f.Url, MediaStem("flyers"),   f.Format, imgExts, f.Size)) flyersCount++;

        // Manuals
        foreach (var m in result.Manuals)
            if (await TryDownload(m.Url, MediaStem("manuals"),  m.Format, docExts, m.Size)) manualsCount++;

        // Physical media (not surfaced in UI yet)
        foreach (var p in result.PhysicalMedia)
            if (await TryDownload(p.Url, MediaStem("physical"),         p.Format, imgExts, p.Size)) physicalCount++;
        foreach (var p in result.PhysicalTexture)
            if (await TryDownload(p.Url, MediaStem("physical-texture"), p.Format, imgExts, p.Size)) physicalTextureCount++;

        return new MediaDownloadSummary(
            Covers:          coversCount,
            Screenshots:     ssCount,
            Fanart:          fanartCount,
            GotVideo:        gotVideo,
            Logos:           logosCount,
            Marquees:        marqueesCount,
            Flyers:          flyersCount,
            Manuals:         manualsCount,
            PhysicalMedia:   physicalCount,
            PhysicalTexture: physicalTextureCount);
    }

    // Swallows per-asset errors; re-throws rate-limit so the outer catch handles it.
    private static async Task<bool> TryDownload(
        string url, string stem, string fmt, IReadOnlyList<string> exts, long? size)
    {
        try
        {
            return await ScreenScraperClient.DownloadMediaAsync(url, stem, fmt, exts, size) is not null;
        }
        catch (ScreenScraperRateLimitException) { throw; }
        catch { return false; }
    }
}
