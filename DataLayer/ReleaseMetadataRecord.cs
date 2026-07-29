using System.Linq;

namespace Arkadia.Data;

public sealed class ReleaseMetadataRecord
{
    public string ReleaseId       { get; init; } = "";
    public string Title           { get; init; } = "";
    public string OriginalTitle   { get; init; } = "";
    public string SortTitle       { get; init; } = "";
    public string Developer       { get; init; } = "";
    public string Publisher       { get; init; } = "";
    public string Year            { get; init; } = "";
    public string Languages       { get; init; } = "";
    /// <summary>Comma-separated alternate titles (from scraper / manual entry).</summary>
    public string AlternateTitles { get; init; } = "";
    /// <summary>Long-form game description (from scraper).</summary>
    public string Description     { get; init; } = "";
    public string Genre           { get; init; } = "";
    public string Subgenre        { get; init; } = "";
    /// <summary>Player count / mode string (e.g. "1-2", "Coop", "Versus").</summary>
    public string Players         { get; init; } = "";
    /// <summary>Release type (e.g. Retail, Prototype, Homebrew, Demo, Hack, Fan Translation).</summary>
    public string ReleaseType     { get; init; } = "";
    public string Rating          { get; init; } = "";
    /// <summary>Free-form curator / user notes. Never overwritten by a scrape.</summary>
    public string Notes           { get; init; } = "";
    /// <summary>UTC ISO-8601 timestamp of last successful scrape; empty if never scraped.</summary>
    public string ScrapedAtUtc    { get; init; } = "";

    /// <summary>Count of populated checklist fields out of 6 (Title…Languages).</summary>
    public int QualityScore => new[] { Title, OriginalTitle, Developer, Publisher, Year, Languages }
        .Count(s => s.Length > 0);
}
