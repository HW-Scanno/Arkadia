using System;

namespace Arkadia.Data;

/// <summary>
/// A proposed reconciliation action held in pending_reconciliations within a DAT-line DB.
/// </summary>
public sealed class PendingReconciliationRecord
{
    public string  Id                  { get; set; } = "";
    public string  NewReleaseId        { get; set; } = "";
    public string  OutdatedReleaseId   { get; set; } = "";

    /// <summary>Artifact being reconciled, if known.</summary>
    public string? ArtifactId          { get; set; }
    /// <summary>Volume the artifact lives on, if known.</summary>
    public string? VolumeId            { get; set; }
    /// <summary>Disk within the volume, if known.</summary>
    public string? DiskId              { get; set; }

    /// <summary>Current relative path of the stored artifact.</summary>
    public string? StoredRelativePath  { get; set; }
    /// <summary>Current filename of the stored artifact.</summary>
    public string? StoredName          { get; set; }

    /// <summary>Filename the artifact should be renamed to.</summary>
    public string  TargetName          { get; set; } = "";
    /// <summary>Relative path the artifact should move to, if different from stored path.</summary>
    public string? TargetRelativePath  { get; set; }

    /// <summary>
    /// Why this reconciliation was proposed.
    /// Allowed values: "content_hash_match".
    /// </summary>
    public string  Reason              { get; set; } = "";

    /// <summary>
    /// Workflow state.
    /// Allowed values: "pending", "applied", "failed", "cancelled".
    /// </summary>
    public string  Status              { get; set; } = "pending";

    public DateTime CreatedAtUtc       { get; set; } = DateTime.UtcNow;
}
