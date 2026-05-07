namespace Arkadia.Providers;

/// <summary>
/// Provider-agnostic representation of a single search candidate returned
/// by a scraper provider's candidate-search step.
/// Populated from provider search results; no media is downloaded, no data saved.
/// </summary>
public sealed record ScraperCandidate
{
    /// <summary>Canonical provider identifier, e.g. "screenscraper".</summary>
    public string  ProviderId       { get; init; } = "";
    /// <summary>Provider's internal ID for this game (opaque string).</summary>
    public string  ProviderGameId   { get; init; } = "";
    public string  Title            { get; init; } = "";
    public string  PlatformName     { get; init; } = "";
    public string  PlatformId       { get; init; } = "";
    public string  Year             { get; init; } = "";
    public string  Developer        { get; init; } = "";
    public string  Publisher        { get; init; } = "";
    /// <summary>Best available region code for this candidate, e.g. "wor", "us".</summary>
    public string  Region           { get; init; } = "";
    public string  Description      { get; init; } = "";
    /// <summary>URL of a thumbnail image (sstitle or box-2D); empty if unavailable.</summary>
    public string  ThumbnailUrl     { get; init; } = "";
    /// <summary>Optional relevance score from 0.0–1.0; null when provider does not supply one.</summary>
    public double? Confidence       { get; init; }
    /// <summary>Raw provider JSON for this candidate; preserved for future deep-parse.</summary>
    public string  RawCandidateJson { get; init; } = "";
}
