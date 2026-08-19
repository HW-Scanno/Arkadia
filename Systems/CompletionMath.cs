using System;

namespace Arkadia.Systems;

/// <summary>
/// Single source of truth for Arkadia's <b>wanted-coverage completion</b> percentage (PO-authoritative rule).
/// Wanted = max(0, releases − unwanted); Completion = present · 100 / wanted using integer division; the value
/// is <c>null</c> ("N/A") when there are no wanted releases. System and Group aggregates feed summed numerators
/// over summed denominators through the same <see cref="Percent"/> — they are a global weighted ratio, never an
/// arithmetic mean of child percentages. Single DAT and Group-leaf use their own (present, releases, unwanted).
/// Pure — no I/O, no Avalonia.
/// </summary>
public static class CompletionMath
{
    /// <summary>Releases the curator intends to keep = all releases minus the unwanted veto (never negative).</summary>
    public static int Wanted(int releases, int unwanted) => Math.Max(0, releases - unwanted);

    /// <summary>
    /// Wanted-coverage percent (integer), or <c>null</c> when there are no wanted releases. Callers pass raw
    /// <paramref name="releases"/> and <paramref name="unwanted"/>; the wanted denominator is derived here so the
    /// rule cannot drift between call sites.
    /// </summary>
    public static int? Percent(int present, int releases, int unwanted)
    {
        int wanted = Wanted(releases, unwanted);
        return wanted > 0 ? present * 100 / wanted : (int?)null;
    }

    /// <summary>Percent from an already-computed wanted denominator (aggregate path: Σ present ÷ Σ wanted).</summary>
    public static int? PercentFromWanted(int present, int wanted)
        => wanted > 0 ? present * 100 / wanted : (int?)null;

    /// <summary>Display string for a completion percent; "N/A" when null (no wanted releases).</summary>
    public static string Text(int? percent) => percent is { } p ? $"{p}%" : "N/A";

    /// <summary>
    /// True when this scope is "missing" in the Group Details sense: it has wanted releases and is not yet
    /// complete. A scope with no wanted releases (N/A) is never missing — there is nothing wanted to complete.
    /// </summary>
    public static bool IsIncomplete(int present, int releases, int unwanted)
    {
        int wanted = Wanted(releases, unwanted);
        return wanted > 0 && present < wanted;
    }
}
