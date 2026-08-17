using System.Collections.Generic;

namespace Arkadia.Volumes;

/// <summary>
/// Pure presentation mapping for the <b>Verify Volume</b> mode of the shared verify dialog (V-G5 micro-fix).
/// The dialog was originally built for the DAT-line "Verify ALL" workflow (VOLUMES/SKIPPED/EXPECTED/… counters,
/// a "READ-ONLY" subtitle). Verify Volume is a single-volume operation that applies recovery actions, so it needs
/// its own header, a truthful subtitle, and counters drawn from <see cref="VolumeVerifyResult"/>. Keeping this
/// mapping pure lets it be unit-tested without an Avalonia UI harness; the dialog only wires strings/ints.
/// </summary>
public static class VolumeVerifyPresentation
{
    /// <summary>Truthful subtitle — Verify Volume is NOT read-only (it restores/moves/reconciles).</summary>
    public const string Subtitle = "Verifies volume contents and applies configured recovery actions";

    /// <summary>The read-only wording used by the genuine DAT-line modes — never shown in Volume mode.</summary>
    public const string ReadOnlySubtitle = "READ-ONLY — no changes will be persisted";

    /// <summary>Header for Volume mode: the volume label plus its owner (Group ⇒ Collection, Single ⇒ DAT Line).</summary>
    public static string Header(string volumeLabel, string ownerDescription, bool isGroup)
        => ownerDescription.Length > 0
            ? $"Volume Verify  —  {volumeLabel}   ·   {(isGroup ? "Collection" : "DAT Line")}: {ownerDescription}"
            : $"Volume Verify  —  {volumeLabel}";

    /// <summary>The seven counter labels for Volume mode (aligned to the dialog's seven stat slots).</summary>
    public static readonly IReadOnlyList<string> CounterLabels =
        ["SCANNED", "RESTORED", "MOVED OUT", "RECONCILED", "VERIFIED", "MISSING", "ERRORS"];

    /// <summary>
    /// Values for the seven Volume counters, in the same slot order as <see cref="CounterLabels"/>. Slots 4/5/6
    /// (VERIFIED/MISSING/ERRORS) align to the dialog's green/red/orange columns. "MOVED OUT" folds all recovery
    /// relocations (unwanted + known-unexpected + unknown); "ERRORS" folds collisions.
    /// </summary>
    public static IReadOnlyList<int> Counters(VolumeVerifyResult r) =>
    [
        r.TotalScanned,
        r.MisplacedRestored,
        r.UnwantedMoved + r.KnownUnexpectedMoved + r.UnknownMoved,
        r.StaleAssignmentsRemoved,
        r.Verified,
        r.Missing,
        r.Errors + r.MisplacedCollisions,
    ];
}
