using System.Linq;

namespace Arkadia.Data;

public sealed class ReleaseMetadataRecord
{
    public string ReleaseId       { get; init; } = "";
    public string Title           { get; init; } = "";
    public string OriginalTitle   { get; init; } = "";
    public string Developer       { get; init; } = "";
    public string Publisher       { get; init; } = "";
    public string Year            { get; init; } = "";
    public string Languages       { get; init; } = "";
    /// <summary>Comma-separated alternate titles (from scraper / manual entry).</summary>
    public string AlternateTitles { get; init; } = "";
    /// <summary>Long-form game description (from scraper).</summary>
    public string Description     { get; init; } = "";
    /// <summary>UTC ISO-8601 timestamp of last successful scrape; empty if never scraped.</summary>
    public string ScrapedAtUtc    { get; init; } = "";

    /// <summary>Count of populated checklist fields out of 6 (Title…Languages).</summary>
    public int QualityScore => new[] { Title, OriginalTitle, Developer, Publisher, Year, Languages }
        .Count(s => s.Length > 0);
}
