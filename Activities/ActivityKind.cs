using System;
using System.Collections.Generic;
using System.Linq;

namespace Arkadia.Activities;

/// <summary>What the application is doing. The workflow only names the KIND; how it looks is decided elsewhere.</summary>
public enum ActivityKind
{
    Loading, Moving, Ingesting, Importing, Verifying,
    Appending, Repairing, Reabsorbing, Creating, Updating,
    Scraping, Downloading, Exporting, Restoring, Processing,
    Cleaning, Purging,
}

/// <summary>
/// The visual family used to render a kind. Deliberately separate from <see cref="ActivityKind"/> (see
/// <see cref="ActivityCatalog.PatternFor"/>): re-mapping a kind to another pattern never touches a workflow.
/// </summary>
public enum ActivityPattern
{
    /// <summary>Cassette-loading multicoloured raster bars in the console border.</summary>
    Raster,
    /// <summary>Pixel blocks flowing left → right (a transfer).</summary>
    BlockFlow,
    /// <summary>Fluctuating pixel columns (incoming data).</summary>
    Columns,
    /// <summary>Small changing hexadecimal characters (terminal / memory dump).</summary>
    HexStream,
    /// <summary>A highlight scanning back and forth across a row of blocks.</summary>
    Scanner,
    /// <summary>Dirt pixels in the console border cleared by a sweeping bar (housekeeping).</summary>
    Sweep,
    /// <summary>Black console, blinking red text and a flowing lava region (destructive purge).</summary>
    Lava,
}

/// <summary>Single place that maps an activity kind to its console text, visual pattern and foreground priority.</summary>
public static class ActivityCatalog
{
    /// <summary>
    /// Foreground priority, lowest first. The highest-priority ACTIVE kind is shown. Rationale: operations that
    /// relocate or rewrite archive/volume content outrank ones that add to the catalog, which outrank read-only
    /// verification, which outranks catalog/metadata housekeeping and network work, which outrank the ambient
    /// "Loading" of views. So a verification running during a Systems refresh shows VERIFYING, and when it ends the
    /// still-running refresh shows LOADING again. Every kind has a distinct rank, so the order is total; two
    /// activities of the SAME kind are tied by age (the older one is reported first) — see ActivityMonitor.
    /// </summary>
    public static readonly IReadOnlyList<ActivityKind> PriorityLowToHigh = new[]
    {
        ActivityKind.Loading,
        ActivityKind.Cleaning,
        ActivityKind.Processing,
        ActivityKind.Downloading,
        ActivityKind.Scraping,
        ActivityKind.Exporting,
        ActivityKind.Creating,
        ActivityKind.Updating,
        ActivityKind.Verifying,
        ActivityKind.Importing,
        ActivityKind.Ingesting,
        ActivityKind.Appending,
        ActivityKind.Repairing,
        ActivityKind.Reabsorbing,
        ActivityKind.Moving,
        ActivityKind.Purging,
        ActivityKind.Restoring,
    };

    private static readonly Dictionary<ActivityKind, int> Ranks =
        PriorityLowToHigh.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);

    /// <summary>Higher = more important foreground.</summary>
    public static int Priority(ActivityKind kind) => Ranks[kind];

    /// <summary>Console text, e.g. <c>"VERIFYING..."</c>.</summary>
    public static string Label(ActivityKind kind) => kind.ToString().ToUpperInvariant() + "...";

    /// <summary>Console text for no activity.</summary>
    public const string ReadyLabel = "READY.";

    public static ActivityPattern PatternFor(ActivityKind kind) => kind switch
    {
        ActivityKind.Loading     => ActivityPattern.Raster,

        ActivityKind.Moving      => ActivityPattern.BlockFlow,
        ActivityKind.Appending   => ActivityPattern.BlockFlow,
        ActivityKind.Reabsorbing => ActivityPattern.BlockFlow,
        ActivityKind.Exporting   => ActivityPattern.BlockFlow,
        ActivityKind.Restoring   => ActivityPattern.BlockFlow,

        ActivityKind.Ingesting   => ActivityPattern.Columns,
        ActivityKind.Downloading => ActivityPattern.Columns,
        ActivityKind.Scraping    => ActivityPattern.Columns,
        ActivityKind.Processing  => ActivityPattern.Columns,

        ActivityKind.Importing   => ActivityPattern.HexStream,
        ActivityKind.Creating    => ActivityPattern.HexStream,
        ActivityKind.Updating    => ActivityPattern.HexStream,

        ActivityKind.Verifying   => ActivityPattern.Scanner,
        ActivityKind.Repairing   => ActivityPattern.Scanner,

        ActivityKind.Cleaning    => ActivityPattern.Sweep,
        ActivityKind.Purging     => ActivityPattern.Lava,

        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
