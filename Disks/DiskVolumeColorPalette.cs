namespace Arkadia.Disks;

/// <summary>
/// Discrete high-contrast palette for per-volume colouring in the Disk Details usage bar
/// and legend.
///
/// Colours are assigned by the volume's INDEX within the disk's tracked-volume list
/// (deterministic: the same order always yields the same colours, and the bar and legend
/// use the same index → identical colour for the same volume). Index assignment — not
/// hashing — guarantees the first N volumes get N distinct colours with no collisions.
///
/// Hues are bright/saturated and remain visible on the dark Arkadia UI; the palette
/// contains no black or white and nothing close to the dark background. Adjacent entries
/// are deliberately different hues so neighbouring segments stay distinguishable. When a
/// disk has more volumes than palette entries, colours cycle only after the palette is
/// exhausted.
/// </summary>
internal static class DiskVolumeColorPalette
{
    internal static readonly string[] Colors =
    {
        "#26C6DA", // cyan
        "#66BB6A", // green
        "#FFCA28", // amber
        "#FF9800", // orange
        "#EF5350", // red
        "#D500F9", // magenta
        "#7E57C2", // violet
        "#42A5F5", // blue
        "#26A69A", // teal
        "#C0CA33", // lime
        "#EC407A", // pink
        "#5C6BC0", // indigo
        "#FFD54F", // gold
        "#FF7043", // coral
    };

    /// <summary>Dim/neutral colour for used-but-untracked disk space (not assigned to a volume).</summary>
    internal const string UntrackedHex = "#3A3A52";

    /// <summary>
    /// Soft-white colour for FREE space. White/near-white is deliberately reserved from the
    /// volume palette, so free space reads unambiguously and stays clearly distinct from every
    /// volume colour and from untracked space — while not being pure harsh white on the dark UI.
    /// </summary>
    internal const string FreeSpaceHex = "#F5F5F5";

    /// <summary>
    /// Hex colour for a volume at <paramref name="index"/>. Cycles after the palette is
    /// exhausted; negative indices are normalised so the result is always a valid entry.
    /// </summary>
    internal static string HexForIndex(int index)
    {
        var n = Colors.Length;
        return Colors[((index % n) + n) % n];
    }
}
