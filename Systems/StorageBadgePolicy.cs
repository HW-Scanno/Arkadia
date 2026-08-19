namespace Arkadia.Systems;

/// <summary>
/// UI-only presentation for the storage attention badges: the English label, the tooltip, and a restrained
/// semantic colour for each lifecycle state plus the independent STAGING flag. No calculation lives here — it maps
/// an already-derived <see cref="ArchiveLifecycle"/> (and the staging boolean) to display text. Colours are
/// foreground/border accents for a compact pill (the row itself is never painted); readability over decoration.
/// </summary>
public static class StorageBadgePolicy
{
    // Restrained semantic accents (border + text on a dark translucent fill). Reused from the Arkadia palette.
    public const string StagingColor     = "#EF5350"; // attention / danger — material still needs processing
    public const string NeedsVolumeColor = "#FF9800"; // warning — unassigned artifacts
    public const string ReadyToMoveColor = "#FFD54F"; // amber — assigned but still local
    public const string ArchivedColor    = "#4CAF50"; // success — physically archived

    public const string StagingLabel = "STAGING";

    public const string StagingTooltip = "Material remains in staging and still requires processing.";

    /// <summary>Label for a lifecycle state; <c>null</c> for <see cref="ArchiveLifecycle.None"/> (no badge).</summary>
    public static string? Label(ArchiveLifecycle lifecycle) => lifecycle switch
    {
        ArchiveLifecycle.NeedsVolume => "NEEDS VOLUME",
        ArchiveLifecycle.ReadyToMove => "READY TO MOVE",
        ArchiveLifecycle.Archived    => "ARCHIVED",
        _                            => null,
    };

    /// <summary>Accent colour (hex) for a lifecycle state; <c>null</c> for <see cref="ArchiveLifecycle.None"/>.</summary>
    public static string? Color(ArchiveLifecycle lifecycle) => lifecycle switch
    {
        ArchiveLifecycle.NeedsVolume => NeedsVolumeColor,
        ArchiveLifecycle.ReadyToMove => ReadyToMoveColor,
        ArchiveLifecycle.Archived    => ArchivedColor,
        _                            => null,
    };

    /// <summary>Tooltip for a lifecycle state; <c>null</c> for <see cref="ArchiveLifecycle.None"/>.</summary>
    public static string? Tooltip(ArchiveLifecycle lifecycle) => lifecycle switch
    {
        ArchiveLifecycle.NeedsVolume =>
            "Some artifacts in the Local Archive are not yet assigned to a volume.",
        ArchiveLifecycle.ReadyToMove =>
            "All current artifacts are assigned, but one or more volumes are still in the Local Archive.",
        ArchiveLifecycle.Archived =>
            "All currently owned content has been assigned to volumes and no relevant volume remains in the "
            + "Local Archive. DAT completion may still be below 100%.",
        _ => null,
    };

    // Completion tooltips (per scope). N/A appends the no-wanted-releases explanation.
    public const string SystemCompletionTooltip =
        "Percentage of wanted releases currently present across all DATs in this system.";
    public const string SingleCompletionTooltip =
        "Percentage of wanted releases currently present in this DAT.";
    public const string GroupCompletionTooltip =
        "Percentage of wanted releases currently present across all leaf DATs in this group.";
    public const string LeafCompletionTooltip =
        "Percentage of wanted releases currently present in this leaf DAT.";
    public const string NaSuffix = " This DAT currently has no wanted releases.";

    /// <summary>Completion tooltip for a scope, appending the N/A explanation when the percent is null.</summary>
    public static string CompletionTooltip(string baseTooltip, int? percent)
        => percent is null ? baseTooltip + NaSuffix : baseTooltip;
}
