using System.Collections.Generic;
using Arkadia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Arkadia.Library;

public sealed class LibraryEntry
{
    public required string Name     { get; init; }
    public required string Platform { get; init; }

    /// <summary>Human-readable status: "Present", "Pending", "Missing", "Lost", or "Outdated".</summary>
    public required string Status   { get; init; }

    public required string Region    { get; init; }
    public required string Languages { get; init; }
    public required string Format   { get; init; }
    public required string Size     { get; init; }

    /// <summary>Raw tier value: "A", "B", "C", or "" for entries with no assigned tier.</summary>
    public required string Tier     { get; init; }

    /// <summary>Resolved flag bitmaps for each language code. Set after theme is loaded; empty until then.</summary>
    public IReadOnlyList<Bitmap> FlagImages { get; set; } = [];

    /// <summary>ROM file entries declared by the DAT for this release. Empty until populated from the store.</summary>
    public IReadOnlyList<ReleaseFileRecord> RomFiles { get; set; } = [];

    /// <summary>Internal release ID in the DAT-line DB. Used for cross-release queries.</summary>
    public string ReleaseId { get; init; } = "";

    /// <summary>Absolute path to the DAT-line SQLite DB that owns this release.</summary>
    public string DbPath { get; init; } = "";

    /// <summary>Display value: "—" for Missing/Pending status or unassigned tier; otherwise the raw tier.</summary>
    public string TierDisplay => Status is "Missing" or "Pending" || Tier == "" ? "—" : Tier;

    /// <summary>Foreground brush derived from <see cref="Status"/> — used directly in the row template.</summary>
    public IBrush StatusBrush => Status switch
    {
        "Present"  => new SolidColorBrush(Color.Parse("#4CAF50")),
        "Pending"  => new SolidColorBrush(Color.Parse("#FFD54F")),
        "Missing"  => new SolidColorBrush(Color.Parse("#FFA726")),
        "Lost"     => new SolidColorBrush(Color.Parse("#EF5350")),
        "Outdated" => new SolidColorBrush(Color.Parse("#888899")),
        _          => new SolidColorBrush(Color.Parse("#888899")),
    };

    /// <summary>Foreground brush for the tier badge — subtle per-tier color treatment.</summary>
    public IBrush TierBrush => TierDisplay switch
    {
        "A" => new SolidColorBrush(Color.Parse("#C8A96E")),
        "B" => new SolidColorBrush(Color.Parse("#F0F0F0")),
        _   => new SolidColorBrush(Color.Parse("#888899")),  // C and "—"
    };
}
