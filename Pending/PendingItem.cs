using System;
using System.Collections.Generic;
using Arkadia.Data;

namespace Arkadia.Pending;

/// <summary>
/// Aggregated view-model for one pending_reconciliations row, enriched with
/// the related releases and DAT-line context.
/// </summary>
public sealed class PendingItem
{
    // ── Reconciliation row ────────────────────────────────────────────────────
    public string   ReconId            { get; init; } = "";
    public string   Reason             { get; init; } = "";
    public DateTime CreatedAtUtc       { get; init; }
    public string   ReconStatus        { get; init; } = "";

    // Physical locator (may be empty in v1)
    public string   ArtifactId         { get; init; } = "";
    public string   VolumeId           { get; init; } = "";
    public string   DiskId             { get; init; } = "";
    public string   StoredRelativePath { get; init; } = "";
    public string   StoredName         { get; init; } = "";
    public string   TargetName         { get; init; } = "";
    public string   TargetRelativePath { get; init; } = "";

    // ── Context ───────────────────────────────────────────────────────────────
    public string   DatLineId          { get; init; } = "";
    public string   DatLineName        { get; init; } = "";   // e.g. "Redump · DVD"
    public string   PlatformId         { get; init; } = "";
    public string   PlatformName       { get; init; } = "";

    // ── New release ───────────────────────────────────────────────────────────
    public ReleaseRecord?               NewRelease      { get; init; }
    public IReadOnlyList<ReleaseFileRecord> NewRomFiles { get; init; } = [];

    // ── Outdated release ──────────────────────────────────────────────────────
    public ReleaseRecord?               OutdatedRelease    { get; init; }
    public IReadOnlyList<ReleaseFileRecord> OutdatedRomFiles { get; init; } = [];

    // ── Computed display values ───────────────────────────────────────────────
    public string ReleaseName   => NewRelease?.Name ?? TargetName;
    public string CreatedLabel  => CreatedAtUtc.ToString("yyyy-MM-dd");
    public string ReasonDisplay => Reason switch
    {
        "content_hash_match" => "Hash Match",
        _ => Reason,
    };
}
